using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PhoneMicBridge.Devices;

namespace PhoneMicBridge.Audio
{
    /// <summary>
    /// HostAudioMonitor captures incoming audio (Rx stream) from the Bluetooth HFP In endpoint
    /// and renders it to the host machine's default multimedia playback device with low latency (&lt; 50ms),
    /// providing loopback monitoring without echoing into the microphone uplink.
    /// </summary>
    public class HostAudioMonitor : IDisposable
    {
        private readonly MMDevice _hfpCaptureDevice;
        private readonly MMDevice _hostPlaybackDevice;
        private readonly object _stateLock = new();

        private WasapiCapture? _wasapiCapture;
        private WasapiOut? _wasapiOut;
        private BufferedWaveProvider? _bufferedWaveProvider;
        private bool _isRunning;
        private bool _isDisposed;

        public bool IsRunning
        {
            get
            {
                lock (_stateLock) return _isRunning;
            }
        }

        public MMDevice HfpCaptureDevice => _hfpCaptureDevice;
        public MMDevice HostPlaybackDevice => _hostPlaybackDevice;

        public event EventHandler<string>? MonitorWarningReceived;

        public HostAudioMonitor(MMDevice hfpCaptureDevice, MMDevice? hostPlaybackDevice = null)
        {
            _hfpCaptureDevice = hfpCaptureDevice ?? throw new ArgumentNullException(nameof(hfpCaptureDevice));
            _hostPlaybackDevice = hostPlaybackDevice ?? DeviceDiscovery.GetDefaultRenderDevice();
        }

        public void Start()
        {
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_isDisposed, this);
                if (_isRunning) return;

                try
                {
                    // Low latency: 25ms buffer for capture and render (< 50ms end-to-end)
                    _wasapiCapture = new WasapiCapture(_hfpCaptureDevice, true, 25);
                    var captureFormat = _wasapiCapture.WaveFormat;

                    _bufferedWaveProvider = new BufferedWaveProvider(captureFormat)
                    {
                        DiscardOnBufferOverflow = true,
                        ReadFully = true
                    };

                    _wasapiCapture.DataAvailable += (s, e) =>
                    {
                        if (!_isRunning) return;
                        _bufferedWaveProvider.AddSamples(e.Buffer, 0, e.BytesRecorded);
                    };

                    var sampleProvider = _bufferedWaveProvider.ToSampleProvider();
                    var playbackFormat = _hostPlaybackDevice.AudioClient.MixFormat;
                    var normalizedProvider = AudioResampler.NormalizeToDestination(
                        sampleProvider,
                        playbackFormat.SampleRate,
                        playbackFormat.Channels
                    );

                    _wasapiOut = new WasapiOut(_hostPlaybackDevice, AudioClientShareMode.Shared, false, 25);
                    _wasapiOut.Init(normalizedProvider);

                    try
                    {
                        _hostPlaybackDevice.AudioSessionManager.AudioSessionControl.SetDuckingPreference(true);
                    }
                    catch { }

                    _wasapiCapture.StartRecording();
                    _wasapiOut.Play();
                    _isRunning = true;
                }
                catch (Exception ex)
                {
                    Stop();
                    MonitorWarningReceived?.Invoke(this, $"Failed to start HostAudioMonitor: {ex.Message}");
                    throw;
                }
            }
        }

        public void Stop()
        {
            lock (_stateLock)
            {
                if (!_isRunning) return;
                _isRunning = false;

                try
                {
                    _wasapiCapture?.StopRecording();
                }
                catch { }

                try
                {
                    _wasapiOut?.Stop();
                }
                catch { }

                try
                {
                    _wasapiCapture?.Dispose();
                }
                catch { }
                _wasapiCapture = null;

                try
                {
                    _wasapiOut?.Dispose();
                }
                catch { }
                _wasapiOut = null;
                _bufferedWaveProvider = null;
            }
        }

        public void Dispose()
        {
            lock (_stateLock)
            {
                if (_isDisposed) return;
                _isDisposed = true;
                Stop();
            }
        }
    }
}
