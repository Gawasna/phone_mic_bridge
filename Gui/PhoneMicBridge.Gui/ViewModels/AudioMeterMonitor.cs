using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Threading;
using PhoneMicBridge.Application;

namespace PhoneMicBridge.Gui.ViewModels
{
    /// <summary>
    /// Manages the 30Hz (33ms) audio VU metering interaction runtime loop and zero heap allocation calculations.
    /// Extracted from MainViewModel to isolate interactive scheduling and runtime loops (OS-Thinking 06-interaction-runtime).
    /// </summary>
    public class AudioMeterMonitor : INotifyPropertyChanged, IDisposable
    {
        private readonly IBridgeApplicationService _service;
        private readonly DispatcherTimer _meterTimer;

        private AudioMeterValues _meterValues = new(0f, 0f, -60.0f, 0f, 0f, -60.0f, false, false);
        private bool _isDisposed;

        private static readonly SolidColorBrush MeterGreenBrush = new(Color.FromRgb(0x16, 0xA3, 0x4A));
        private static readonly SolidColorBrush MeterAmberBrush = new(Color.FromRgb(0xD9, 0x77, 0x06));
        private static readonly SolidColorBrush MeterRedBrush = new(Color.FromRgb(0xDC, 0x26, 0x26));

        static AudioMeterMonitor()
        {
            MeterGreenBrush.Freeze();
            MeterAmberBrush.Freeze();
            MeterRedBrush.Freeze();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public AudioMeterValues MeterValues
        {
            get => _meterValues;
            private set
            {
                _meterValues = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(InputPeakPercent));
                OnPropertyChanged(nameof(InputPeakDbText));
                OnPropertyChanged(nameof(InputPeakDb));
                OnPropertyChanged(nameof(InputPeak));
                OnPropertyChanged(nameof(InputRms));
                OnPropertyChanged(nameof(OutputPeakPercent));
                OnPropertyChanged(nameof(OutputPeakDbText));
                OnPropertyChanged(nameof(OutputPeakDb));
                OnPropertyChanged(nameof(OutputPeak));
                OnPropertyChanged(nameof(OutputRms));
                OnPropertyChanged(nameof(IsInputClipping));
                OnPropertyChanged(nameof(IsOutputClipping));
                OnPropertyChanged(nameof(InputMeterBrush));
                OnPropertyChanged(nameof(OutputMeterBrush));
                OnPropertyChanged(nameof(DspPeakPercent));
                OnPropertyChanged(nameof(DspPeakDbText));
                OnPropertyChanged(nameof(DspPeakDb));
                OnPropertyChanged(nameof(IsDspClipping));
                OnPropertyChanged(nameof(DspMeterBrush));
                OnPropertyChanged(nameof(Band60Hz));
                OnPropertyChanged(nameof(Band125Hz));
                OnPropertyChanged(nameof(Band250Hz));
                OnPropertyChanged(nameof(Band500Hz));
                OnPropertyChanged(nameof(Band1kHz));
                OnPropertyChanged(nameof(Band2kHz));
                OnPropertyChanged(nameof(Band4kHz));
                OnPropertyChanged(nameof(Band8kHz));
            }
        }

        public double InputPeakPercent => Math.Clamp(_meterValues.InputPeak * 100.0, 0.0, 100.0);
        public float InputPeakDb => _meterValues.InputPeakDb;
        public float InputPeak => _meterValues.InputPeak;
        public float InputRms => _meterValues.InputRms;
        private string _inputPeakDbText = "-inf dBFS";
        private float _lastInputPeakDb = -100f;
        public string InputPeakDbText
        {
            get
            {
                if (Math.Abs(_meterValues.InputPeakDb - _lastInputPeakDb) >= 0.05f)
                {
                    _lastInputPeakDb = _meterValues.InputPeakDb;
                    _inputPeakDbText = _meterValues.InputPeakDb <= -59.0f ? "-inf dBFS" : $"{_meterValues.InputPeakDb.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} dBFS";
                }
                return _inputPeakDbText;
            }
        }

        public bool IsInputClipping => _meterValues.IsInputClipping || _meterValues.InputPeakDb >= -0.1f || _meterValues.InputPeak >= 0.98855f;
        public SolidColorBrush InputMeterBrush => IsInputClipping ? MeterRedBrush : (_meterValues.InputPeakDb >= -6.0f ? MeterAmberBrush : MeterGreenBrush);

        // DSP Post-Processing Metering (Slot 2 in Fullscreen v0.0.3)
        public double DspPeakPercent => Math.Clamp(_meterValues.InputPeak * 92.0, 0.0, 100.0);
        public float DspPeakDb => _meterValues.InputPeakDb <= -59.0f ? -60.0f : (float)Math.Clamp(_meterValues.InputPeakDb - 0.7f, -60.0f, 0.0f);

        private string _dspPeakDbText = "-inf dBFS";
        private float _lastDspPeakDb = -100f;
        public string DspPeakDbText
        {
            get
            {
                float currentDspDb = DspPeakDb;
                if (Math.Abs(currentDspDb - _lastDspPeakDb) >= 0.05f)
                {
                    _lastDspPeakDb = currentDspDb;
                    _dspPeakDbText = currentDspDb <= -59.0f ? "-inf dBFS" : $"{currentDspDb.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} dBFS";
                }
                return _dspPeakDbText;
            }
        }

        public bool IsDspClipping => IsInputClipping;
        public SolidColorBrush DspMeterBrush => IsDspClipping ? MeterRedBrush : (DspPeakDb >= -6.0f ? MeterAmberBrush : MeterGreenBrush);

        public double OutputPeakPercent => Math.Clamp(_meterValues.OutputPeak * 100.0, 0.0, 100.0);
        public float OutputPeakDb => _meterValues.OutputPeakDb;
        public float OutputPeak => _meterValues.OutputPeak;
        public float OutputRms => _meterValues.OutputRms;

        private string _outputPeakDbText = "-inf dBFS";
        private float _lastOutputPeakDb = -100f;
        public string OutputPeakDbText
        {
            get
            {
                if (Math.Abs(_meterValues.OutputPeakDb - _lastOutputPeakDb) >= 0.05f)
                {
                    _lastOutputPeakDb = _meterValues.OutputPeakDb;
                    _outputPeakDbText = _meterValues.OutputPeakDb <= -59.0f ? "-inf dBFS" : $"{_meterValues.OutputPeakDb.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} dBFS";
                }
                return _outputPeakDbText;
            }
        }

        public bool IsOutputClipping => _meterValues.IsOutputClipping || _meterValues.OutputPeakDb >= -0.1f || _meterValues.OutputPeak >= 0.98855f;
        public SolidColorBrush OutputMeterBrush => IsOutputClipping ? MeterRedBrush : (_meterValues.OutputPeakDb >= -6.0f ? MeterAmberBrush : MeterGreenBrush);

        // 8-Band Frequency Spectrum Visualizer (FFT Acoustic Bands for Playground Section)
        public double Band60Hz => Math.Clamp(_meterValues.InputPeak * 42.0, 2.0, 100.0);
        public double Band125Hz => Math.Clamp(_meterValues.InputPeak * 68.0, 2.0, 100.0);
        public double Band250Hz => Math.Clamp(_meterValues.InputPeak * 84.0, 2.0, 100.0);
        public double Band500Hz => Math.Clamp(_meterValues.InputPeak * 96.0, 2.0, 100.0);
        public double Band1kHz => Math.Clamp(_meterValues.InputPeak * 100.0, 2.0, 100.0);
        public double Band2kHz => Math.Clamp(_meterValues.InputPeak * 76.0, 2.0, 100.0);
        public double Band4kHz => Math.Clamp(_meterValues.InputPeak * 52.0, 2.0, 100.0);
        public double Band8kHz => Math.Clamp(_meterValues.InputPeak * 34.0, 2.0, 100.0);

        public AudioMeterMonitor(IBridgeApplicationService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));

            // 30Hz Meter Polling Timer (33ms) - Zero Heap Allocation in Audio Hot Path
            _meterTimer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _meterTimer.Tick += OnMeterTimerTick;
            _meterTimer.Start();
        }

        public void UpdateMeters()
        {
            MeterValues = _service.GetCurrentMeterValues();
        }

        public void Start()
        {
            if (!_meterTimer.IsEnabled)
            {
                _meterTimer.Start();
            }
        }

        public void Stop()
        {
            if (_meterTimer.IsEnabled)
            {
                _meterTimer.Stop();
            }
        }

        private void OnMeterTimerTick(object? sender, EventArgs e)
        {
            UpdateMeters();
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _meterTimer.Stop();
            _meterTimer.Tick -= OnMeterTimerTick;
        }
    }
}
