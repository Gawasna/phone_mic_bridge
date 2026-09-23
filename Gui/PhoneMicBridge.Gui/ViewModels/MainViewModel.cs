using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using PhoneMicBridge.Application;
using PhoneMicBridge.Core;

namespace PhoneMicBridge.Gui.ViewModels
{
    public class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly IBridgeApplicationService _service;
        private readonly DispatcherTimer _meterTimer;

        private BridgeUiSnapshot _snapshot;
        private AudioMeterValues _meterValues = new(0f, 0f, -60.0f, 0f, 0f, -60.0f, false, false);

        private ObservableCollection<AudioDeviceDto> _captureDevices = new();
        private ObservableCollection<AudioDeviceDto> _renderDevices = new();
        private AudioDeviceDto? _selectedCaptureDevice;
        private AudioDeviceDto? _selectedRenderDevice;
        private static readonly System.Windows.Media.SolidColorBrush MeterGreenBrush = new(System.Windows.Media.Color.FromRgb(0x10, 0xB9, 0x81));
        private static readonly System.Windows.Media.SolidColorBrush MeterAmberBrush = new(System.Windows.Media.Color.FromRgb(0xF5, 0x9E, 0x0B));
        private static readonly System.Windows.Media.SolidColorBrush MeterRedBrush = new(System.Windows.Media.Color.FromRgb(0xEF, 0x44, 0x44));

        static MainViewModel()
        {
            MeterGreenBrush.Freeze();
            MeterAmberBrush.Freeze();
            MeterRedBrush.Freeze();
        }

        private bool _isCompactMode;
        private bool _isDisposed;

        public event PropertyChangedEventHandler? PropertyChanged;

        public BridgeUiSnapshot Snapshot
        {
            get => _snapshot;
            private set
            {
                if (_snapshot != value)
                {
                    _snapshot = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StateText));
                    OnPropertyChanged(nameof(StateColorKey));
                    OnPropertyChanged(nameof(CanStart));
                    OnPropertyChanged(nameof(CanStop));
                    OnPropertyChanged(nameof(HasError));
                    OnPropertyChanged(nameof(ErrorMessage));
                    OnPropertyChanged(nameof(EndpointStatusText));
                    OnPropertyChanged(nameof(BluetoothStatusText));
                    OnPropertyChanged(nameof(DeliveryStatusText));
                    OnPropertyChanged(nameof(PreflightBadgeText));
                    OnPropertyChanged(nameof(PreflightBadgeColorKey));
                    OnPropertyChanged(nameof(StartButtonTooltip));
                    OnPropertyChanged(nameof(SafetyInterlockStatusText));

                    (StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (StopCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

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
            }
        }

        public ObservableCollection<AudioDeviceDto> CaptureDevices => _captureDevices;
        public ObservableCollection<AudioDeviceDto> RenderDevices => _renderDevices;

        public AudioDeviceDto? SelectedCaptureDevice
        {
            get => _selectedCaptureDevice;
            set
            {
                if (_selectedCaptureDevice != value)
                {
                    _selectedCaptureDevice = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedCaptureDeviceId));
                    OnDeviceSelectionChanged();
                }
            }
        }

        public AudioDeviceDto? SelectedRenderDevice
        {
            get => _selectedRenderDevice;
            set
            {
                if (_selectedRenderDevice != value)
                {
                    _selectedRenderDevice = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedRenderDeviceId));
                    OnDeviceSelectionChanged();
                }
            }
        }

        public string? SelectedCaptureDeviceId => _selectedCaptureDevice?.Id;
        public string? SelectedRenderDeviceId => _selectedRenderDevice?.Id;

        public bool IsCompactMode
        {
            get => _isCompactMode;
            set
            {
                if (_isCompactMode != value)
                {
                    _isCompactMode = value;
                    OnPropertyChanged();
                }
            }
        }

        // Computed status presentation properties
        public string StateText => _snapshot.State switch
        {
            BridgeState.Disconnected => "DISCONNECTED",
            BridgeState.Connecting => "CONNECTING...",
            BridgeState.Streaming => "STREAMING ACTIVE",
            BridgeState.Paused => "PAUSED",
            BridgeState.Stopped => "STOPPED",
            BridgeState.Error => "ERROR",
            _ => _snapshot.State.ToString().ToUpperInvariant()
        };

        public string StateColorKey => _snapshot.State switch
        {
            BridgeState.Streaming => "PassBgBrush",
            BridgeState.Connecting => "UnknownBgBrush",
            BridgeState.Error => "FailBgBrush",
            _ => "PanelBrush"
        };

        public bool CanStart => _snapshot.CanStartSession && _snapshot.State != BridgeState.Streaming && _snapshot.State != BridgeState.Connecting;
        public bool CanStop => _snapshot.State == BridgeState.Streaming || _snapshot.State == BridgeState.Connecting;

        public bool HasError => !string.IsNullOrEmpty(_snapshot.LastError);
        public string ErrorMessage => _snapshot.LastError ?? string.Empty;

        public string EndpointStatusText => _snapshot.RenderStatus switch
        {
            EndpointRenderStatus.Active => "Endpoint Render Active",
            EndpointRenderStatus.Error => "ERROR (Endpoint Failed)",
            _ => "INACTIVE"
        };

        public string BluetoothStatusText => _snapshot.BluetoothStatus switch
        {
            BluetoothTransportStatus.ScoActive => "HFP / SCO Channel Active",
            BluetoothTransportStatus.Connecting => "CONNECTING (Negotiating HFP)",
            BluetoothTransportStatus.Error => "ERROR (Link Dropped)",
            _ => "DISCONNECTED"
        };

        // Truthful Status Display: Always unconfirmed without companion ACK
        public string DeliveryStatusText => _snapshot.DeliveryStatus switch
        {
            DeliveryAckStatus.Confirmed => "CONFIRMED (Delivered to Phone)",
            _ => "UNVERIFIED (No Companion ACK Protocol)"
        };

        public string PreflightBadgeText => _snapshot.PreflightOverall switch
        {
            DiagnosticOverallStatus.Pass => "ALL CHECKS PASSED",
            DiagnosticOverallStatus.Fail => "CHECKS FAILED",
            DiagnosticOverallStatus.InProgress => "CHECKING...",
            _ => "NOT VERIFIED"
        };

        public string PreflightBadgeColorKey => _snapshot.PreflightOverall switch
        {
            DiagnosticOverallStatus.Pass => "PassBgBrush",
            DiagnosticOverallStatus.Fail => "FailBgBrush",
            _ => "UnknownBgBrush"
        };

        public string StartButtonTooltip => _snapshot.PreflightOverall switch
        {
            DiagnosticOverallStatus.Fail => "Cannot start bridge: Pre-flight diagnostics failed. Please resolve reported issues in the diagnostics panel.",
            DiagnosticOverallStatus.InProgress => "Diagnostics in progress. Please wait...",
            _ => _snapshot.State switch
            {
                BridgeState.Streaming => "Audio bridge session is currently active.",
                BridgeState.Connecting => "Audio bridge is currently connecting...",
                _ => "Start audio bridge session."
            }
        };

        public string SafetyInterlockStatusText => _snapshot.PreflightOverall switch
        {
            DiagnosticOverallStatus.Fail => "SAFETY INTERLOCK: START BLOCKED (Pre-flight checks failed)",
            DiagnosticOverallStatus.Unknown => "DIAGNOSTICS ADVISORY: Check status unverified",
            DiagnosticOverallStatus.InProgress => "DIAGNOSTICS: In progress...",
            _ => "SAFETY INTERLOCK: READY (All checks passed)"
        };

        // Audio Meter Helpers (Zero Allocation)
        public double InputPeakPercent => Math.Clamp(_meterValues.InputPeak * 100.0, 0.0, 100.0);
        public float InputPeakDb => _meterValues.InputPeakDb;
        public float InputPeak => _meterValues.InputPeak;
        public float InputRms => _meterValues.InputRms;
        public string InputPeakDbText => _meterValues.InputPeakDb <= -59.0f ? "-inf dBFS" : $"{_meterValues.InputPeakDb.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} dBFS";
        public bool IsInputClipping => _meterValues.IsInputClipping || _meterValues.InputPeakDb >= -0.1f || _meterValues.InputPeak >= 0.98855f;
        public System.Windows.Media.Brush InputMeterBrush => IsInputClipping ? MeterRedBrush : (_meterValues.InputPeakDb >= -6.0f ? MeterAmberBrush : MeterGreenBrush);

        public double OutputPeakPercent => Math.Clamp(_meterValues.OutputPeak * 100.0, 0.0, 100.0);
        public float OutputPeakDb => _meterValues.OutputPeakDb;
        public float OutputPeak => _meterValues.OutputPeak;
        public float OutputRms => _meterValues.OutputRms;
        public string OutputPeakDbText => _meterValues.OutputPeakDb <= -59.0f ? "-inf dBFS" : $"{_meterValues.OutputPeakDb.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} dBFS";
        public bool IsOutputClipping => _meterValues.IsOutputClipping || _meterValues.OutputPeakDb >= -0.1f || _meterValues.OutputPeak >= 0.98855f;
        public System.Windows.Media.Brush OutputMeterBrush => IsOutputClipping ? MeterRedBrush : (_meterValues.OutputPeakDb >= -6.0f ? MeterAmberBrush : MeterGreenBrush);

        public void UpdateMeters()
        {
            MeterValues = _service.GetCurrentMeterValues();
        }

        // Source Controls - Microphone
        public bool IsMicEnabled
        {
            get => _snapshot.IsMicEnabled;
            set
            {
                if (_snapshot.IsMicEnabled != value)
                {
                    _service.SetMicEnabled(value);
                }
            }
        }

        public bool IsMicMuted
        {
            get => !_snapshot.IsMicEnabled;
            set
            {
                if (_snapshot.IsMicEnabled == value)
                {
                    _service.SetMicEnabled(!value);
                }
            }
        }

        public string MicMuteButtonText => IsMicMuted ? "UNMUTE MIC" : "MUTE MIC";
        public string MicStatusText => IsMicMuted ? "MUTED" : "UNMUTED";

        public float MicGain
        {
            get => _snapshot.MicGain;
            set
            {
                if (Math.Abs(_snapshot.MicGain - value) > 0.001f)
                {
                    _service.SetMicGain(value);
                }
            }
        }

        public string MicGainText => $"{MicGain.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}x";

        public void ToggleMic() => _service.ToggleMic();
        public void SetMicMuted(bool muted) => _service.SetMicMuted(muted);
        public void SetMicEnabled(bool enabled) => _service.SetMicEnabled(enabled);
        public void SetMicGain(float gain) => _service.SetMicGain(gain);

        // Source Controls - Test Tone Generator
        public bool IsToneEnabled
        {
            get => _snapshot.IsToneEnabled;
            set
            {
                if (_snapshot.IsToneEnabled != value)
                {
                    _service.SetToneEnabled(value, _snapshot.ToneFrequency, _snapshot.ToneGain);
                }
            }
        }

        public float ToneGain
        {
            get => _snapshot.ToneGain;
            set
            {
                if (Math.Abs(_snapshot.ToneGain - value) > 0.001f)
                {
                    _service.SetToneGain(value);
                }
            }
        }

        public string ToneGainText => $"{ToneGain.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}x";
        public double ToneFrequency => _snapshot.ToneFrequency;
        public string ToneStatusText => IsToneEnabled ? "TONE ACTIVE (1000 Hz, -12 dBFS)" : "OFF";
        public string ToneButtonText => IsToneEnabled ? "STOP TONE" : "START TONE";

        public void ToggleTestTone() => _service.ToggleTestTone();
        public void SetToneEnabled(bool enabled) => _service.SetToneEnabled(enabled);
        public void SetToneGain(float gain) => _service.SetToneGain(gain);

        // Source Controls - Audio File Player
        public bool HasAudioFile => _snapshot.HasAudioFile;
        public bool IsFilePlaying => _snapshot.IsFilePlaying;
        public string? AudioFileName => _snapshot.AudioFileName;
        public string? TestAudioFileName => _snapshot.AudioFileName;
        public string AudioFileStatusText => string.IsNullOrEmpty(_snapshot.AudioFileName)
            ? "No file loaded"
            : (_snapshot.IsFilePlaying ? $"Playing: {_snapshot.AudioFileName}" : $"Loaded: {_snapshot.AudioFileName} (Stopped)");
        public string PlayButtonText => _snapshot.IsFilePlaying ? "STOP AUDIO" : "PLAY AUDIO";

        public float FileGain
        {
            get => _snapshot.FileGain;
            set
            {
                if (Math.Abs(_snapshot.FileGain - value) > 0.001f)
                {
                    _service.SetFileGain(value);
                }
            }
        }

        public string FileGainText => $"{FileGain.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}x";

        public void PlayTestAudio(string filePath) => _service.PlayTestAudio(filePath);
        public Task<bool> PlayTestAudioAsync(string filePath) => _service.PlayTestAudioAsync(filePath);
        public void StopTestAudio() => _service.StopTestAudio();
        public void ToggleFilePlayback() => _service.ToggleFilePlayback();
        public void SetFileGain(float gain) => _service.SetFileGain(gain);

        // Commands
        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand RunDiagnosticsCommand { get; }
        public ICommand RefreshDevicesCommand { get; }
        public ICommand ClearErrorCommand { get; }
        public ICommand ToggleMicCommand { get; }
        public ICommand ToggleTestToneCommand { get; }
        public ICommand ToggleFilePlaybackCommand { get; }
        public ICommand PlayTestAudioCommand { get; }
        public ICommand StopTestAudioCommand { get; }
        public ICommand BrowseAudioFileCommand { get; }

        public MainViewModel() : this(null)
        {
        }

        public MainViewModel(IBridgeApplicationService? service)
        {
            _service = service ?? new BridgeApplicationService();
            _snapshot = _service.CurrentSnapshot;
            _meterValues = _service.GetCurrentMeterValues();
            _service.SnapshotChanged += OnServiceSnapshotChanged;

            StartCommand = new AsyncRelayCommand(async () =>
            {
                try
                {
                    await _service.StartBridgeAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Start Session Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }, () => CanStart);

            StopCommand = new AsyncRelayCommand(async () =>
            {
                await _service.StopBridgeAsync().ConfigureAwait(true);
            }, () => CanStop);

            RunDiagnosticsCommand = new AsyncRelayCommand(async () =>
            {
                await _service.RunPreflightDiagnosticsAsync().ConfigureAwait(true);
            });

            RefreshDevicesCommand = new AsyncRelayCommand(LoadDevicesAsync);

            ClearErrorCommand = new RelayCommand(() =>
            {
                _service.ClearError();
            });

            ToggleMicCommand = new RelayCommand(ToggleMic);
            ToggleTestToneCommand = new RelayCommand(ToggleTestTone);
            ToggleFilePlaybackCommand = new RelayCommand(ToggleFilePlayback);
            PlayTestAudioCommand = new AsyncRelayCommand(PlayOrToggleAudioFileAsync);
            StopTestAudioCommand = new RelayCommand(StopTestAudio);
            BrowseAudioFileCommand = new AsyncRelayCommand(BrowseAndLoadAudioFileAsync);

            // 30Hz Meter Polling Timer (33ms) - Zero Heap Allocation in Audio Hot Path
            _meterTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(33)
            };
            _meterTimer.Tick += OnMeterTimerTick;
            _meterTimer.Start();

            // Initial load with safe try/catch
            _ = InitializeAsync();
        }

        private async Task PlayOrToggleAudioFileAsync()
        {
            if (!_snapshot.HasAudioFile)
            {
                await BrowseAndLoadAudioFileAsync().ConfigureAwait(true);
            }
            else
            {
                _service.ToggleFilePlayback();
            }
        }

        private async Task BrowseAndLoadAudioFileAsync()
        {
            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "Select Audio File for Bridge Playback",
                Filter = "Audio Files (*.wav;*.mp3)|*.wav;*.mp3|All Files (*.*)|*.*",
                CheckFileExists = true
            };

            if (dialog.ShowDialog() == true)
            {
                await _service.LoadAudioFileAsync(dialog.FileName).ConfigureAwait(true);
            }
        }

        private async Task InitializeAsync()
        {
            try
            {
                await LoadDevicesAsync().ConfigureAwait(true);
                await _service.RunPreflightDiagnosticsAsync().ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"Initialization error: {ex.Message}");
            }
        }

        public async Task LoadDevicesAsync()
        {
            string? prevCaptureId = _selectedCaptureDevice?.Id ?? _snapshot.SelectedCaptureDeviceId;
            string? prevRenderId = _selectedRenderDevice?.Id ?? _snapshot.SelectedRenderDeviceId;

            var captureList = await _service.GetCaptureDevicesAsync().ConfigureAwait(true);
            var renderList = await _service.GetRenderDevicesAsync().ConfigureAwait(true);

            _captureDevices.Clear();
            foreach (var d in captureList) _captureDevices.Add(d);

            _renderDevices.Clear();
            foreach (var d in renderList) _renderDevices.Add(d);

            // Select capture device by stable ID if previous choice exists; otherwise default/active
            if (!string.IsNullOrEmpty(prevCaptureId))
            {
                _selectedCaptureDevice = _captureDevices.FirstOrDefault(c => string.Equals(c.Id, prevCaptureId, StringComparison.OrdinalIgnoreCase));
            }
            if (_selectedCaptureDevice == null)
            {
                _selectedCaptureDevice = _captureDevices.FirstOrDefault(c => c.IsDefault)
                                      ?? _captureDevices.FirstOrDefault(c => c.IsActive);
            }
            OnPropertyChanged(nameof(SelectedCaptureDevice));
            OnPropertyChanged(nameof(SelectedCaptureDeviceId));

            // Select render device by stable ID if previous choice exists; otherwise preferred HFP
            if (!string.IsNullOrEmpty(prevRenderId))
            {
                _selectedRenderDevice = _renderDevices.FirstOrDefault(r => string.Equals(r.Id, prevRenderId, StringComparison.OrdinalIgnoreCase));
            }
            if (_selectedRenderDevice == null)
            {
                _selectedRenderDevice = _renderDevices.FirstOrDefault(r => r.IsTargetHfp && r.IsActive)
                                     ?? _renderDevices.FirstOrDefault(r => r.IsTargetHfp)
                                     ?? _renderDevices.FirstOrDefault(r => r.IsActive);
            }
            OnPropertyChanged(nameof(SelectedRenderDevice));
            OnPropertyChanged(nameof(SelectedRenderDeviceId));

            if (_selectedCaptureDevice != null || _selectedRenderDevice != null)
            {
                await _service.SelectDevicesAsync(_selectedCaptureDevice?.Id, _selectedRenderDevice?.Id).ConfigureAwait(true);
            }
        }

        private void OnDeviceSelectionChanged()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _service.SelectDevicesAsync(_selectedCaptureDevice?.Id, _selectedRenderDevice?.Id).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Device selection error: {ex.Message}");
                }
            });
        }

        private void OnMeterTimerTick(object? sender, EventArgs e)
        {
            UpdateMeters();
        }

        private void OnServiceSnapshotChanged(object? sender, BridgeUiSnapshot snapshot)
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.InvokeAsync(() => ApplySnapshot(snapshot));
            }
            else
            {
                ApplySnapshot(snapshot);
            }
        }

        private void ApplySnapshot(BridgeUiSnapshot snapshot)
        {
            Snapshot = snapshot;
            OnPropertyChanged(nameof(IsMicEnabled));
            OnPropertyChanged(nameof(IsMicMuted));
            OnPropertyChanged(nameof(MicMuteButtonText));
            OnPropertyChanged(nameof(MicStatusText));
            OnPropertyChanged(nameof(MicGain));
            OnPropertyChanged(nameof(MicGainText));

            OnPropertyChanged(nameof(IsToneEnabled));
            OnPropertyChanged(nameof(ToneGain));
            OnPropertyChanged(nameof(ToneGainText));
            OnPropertyChanged(nameof(ToneFrequency));
            OnPropertyChanged(nameof(ToneStatusText));
            OnPropertyChanged(nameof(ToneButtonText));

            OnPropertyChanged(nameof(HasAudioFile));
            OnPropertyChanged(nameof(IsFilePlaying));
            OnPropertyChanged(nameof(AudioFileName));
            OnPropertyChanged(nameof(TestAudioFileName));
            OnPropertyChanged(nameof(AudioFileStatusText));
            OnPropertyChanged(nameof(PlayButtonText));
            OnPropertyChanged(nameof(FileGain));
            OnPropertyChanged(nameof(FileGainText));
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
            _service.SnapshotChanged -= OnServiceSnapshotChanged;
            _service.Dispose();
        }
    }
}
