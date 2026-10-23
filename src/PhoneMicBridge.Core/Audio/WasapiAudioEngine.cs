using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using PhoneMicBridge.Core;
using PhoneMicBridge.Devices;

namespace PhoneMicBridge.Audio
{
    public class WasapiAudioEngine : IAudioEngine
    {
        private readonly MMDevice _renderDevice;
        private readonly MMDevice? _captureDevice;
        private readonly DeviceNotificationWatcher _deviceWatcher;
        private readonly BridgeStateMachine _stateMachine = new(BridgeState.Disconnected);

        private WasapiOut? _wasapiOut;
        private WasapiCapture? _wasapiCapture;
        private BufferedWaveProvider? _captureBuffer;
        private MMDevice? _hfpDuplexCaptureDevice;
        private WasapiCapture? _hfpDuplexCapture;
        private bool _isDisposed;
        
        // Telemetry for bidirectional traffic monitoring
        private long _hfpCaptureByteCount = 0;
        private long _hfpRenderByteCount = 0;
        private DateTime _lastTelemetryLog = DateTime.UtcNow;

        public BridgeState State => _stateMachine.CurrentState;
        public event EventHandler<BridgeState>? StateChanged;
        public event EventHandler<string>? DeviceWarningReceived;

        public MixingPipeline Pipeline { get; }
        public AudioMeter OutputMeter { get; } = new();
        public AudioMeter MicMeter { get; } = new();

        public MMDevice RenderDevice => _renderDevice;
        public MMDevice? CaptureDevice => _captureDevice;
        public WaveFormat RenderFormat => _renderDevice.AudioClient.MixFormat;

        public WasapiAudioEngine(MMDevice renderDevice, MMDevice? captureDevice = null)
        {
            _renderDevice = renderDevice ?? throw new ArgumentNullException(nameof(renderDevice));
            _captureDevice = captureDevice;

            int targetRate = RenderFormat.SampleRate;
            Pipeline = new MixingPipeline(targetRate, 1);

            _stateMachine.StateChanged += (s, e) => StateChanged?.Invoke(this, e);

            _deviceWatcher = new DeviceNotificationWatcher();
            _deviceWatcher.DeviceStateChanged += OnDeviceStateChanged;
            _deviceWatcher.DeviceRemoved += OnDeviceRemoved;
        }

        private void OnDeviceRemoved(object? sender, DeviceNotificationEventArgs e)
        {
            if (_isDisposed) return;

            bool isRender = string.Equals(e.DeviceId, _renderDevice.ID, StringComparison.OrdinalIgnoreCase);
            bool isCapture = _captureDevice != null && string.Equals(e.DeviceId, _captureDevice.ID, StringComparison.OrdinalIgnoreCase);

            if (isRender)
            {
                DeviceWarningReceived?.Invoke(this, $"Target HFP device removed: {e.DeviceId}");
                if (State == BridgeState.Streaming)
                {
                    Stop();
                    _stateMachine.TryTransitionTo(BridgeState.Error);
                }
            }
            else if (isCapture)
            {
                DeviceWarningReceived?.Invoke(this, $"Microphone capture device removed: {e.DeviceId}");
                Pipeline.SetMicEnabled(false);
            }
        }

        private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e)
        {
            if (_isDisposed) return;

            // Mechanism: Detect if target render device was unplugged or disconnected
            if (string.Equals(e.DeviceId, _renderDevice.ID, StringComparison.OrdinalIgnoreCase))
            {
                if (e.NewState != DeviceState.Active)
                {
                    DeviceWarningReceived?.Invoke(this, $"Target HFP device state changed to: {e.NewState}");
                    if (State == BridgeState.Streaming)
                    {
                        Stop();
                        _stateMachine.TryTransitionTo(BridgeState.Error);
                    }
                }
            }
            else if (_captureDevice != null && string.Equals(e.DeviceId, _captureDevice.ID, StringComparison.OrdinalIgnoreCase))
            {
                if (e.NewState != DeviceState.Active)
                {
                    DeviceWarningReceived?.Invoke(this, $"Microphone capture device state changed to: {e.NewState}");
                    Pipeline.SetMicEnabled(false);
                }
            }
        }

        public void SetupMicCapture(bool initialEnabled = true)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (_captureDevice == null) return;

            if (_captureDevice.DataFlow == DataFlow.Render)
            {
                _wasapiCapture = new WasapiLoopbackCapture(_captureDevice);
            }
            else
            {
                _wasapiCapture = new WasapiCapture(_captureDevice, true, 50);
            }
            _captureBuffer = new BufferedWaveProvider(_wasapiCapture.WaveFormat)
            {
                DiscardOnBufferOverflow = true,
                ReadFully = true
            };

            try
            {
                _captureDevice.AudioSessionManager.AudioSessionControl.SetDuckingPreference(true);
            }
            catch { }

            _wasapiCapture.DataAvailable += (s, e) =>
            {
                if (Pipeline.IsMicEnabled)
                {
                    MicMeter.UpdateFromRawBuffer(e.Buffer, e.BytesRecorded, _wasapiCapture.WaveFormat, Pipeline.MicGain);
                    _captureBuffer.AddSamples(e.Buffer, 0, e.BytesRecorded);
                }
            };

            Pipeline.AttachMicSource(_captureBuffer.ToSampleProvider(), initialEnabled);
        }

        public void Start()
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            if (State == BridgeState.Streaming) return;

            _stateMachine.TransitionTo(BridgeState.Connecting);

            try
            {
                var baseProvider = Pipeline.GetOutputSampleProvider();
                var meterProvider = OutputMeter.Wrap(baseProvider);
                var telemetryProvider = new TelemetrySampleProvider(meterProvider, bytes => 
                {
                    System.Threading.Interlocked.Add(ref _hfpRenderByteCount, bytes);
                });

                _wasapiOut = new WasapiOut(_renderDevice, AudioClientShareMode.Shared, false, 50);
                _wasapiOut.Init(telemetryProvider);

                try
                {
                    _renderDevice.AudioSessionManager.AudioSessionControl.SetDuckingPreference(true);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[DUCKING] Failed to set ducking preference on render device: {ex.Message}");
                }
                
                _wasapiOut.PlaybackStopped += (s, e) =>
                {
                    Console.WriteLine($"[HFP-RENDER] Playback stopped. Total bytes written: {_hfpRenderByteCount}");
                };

                // Full-Duplex HFP keep-alive: BthHFAud.sys requires active capture stream to maintain SCO link
                _hfpDuplexCaptureDevice ??= DeviceDiscovery.FindMatchingHfpCaptureDevice(_renderDevice);
                if (_hfpDuplexCaptureDevice != null)
                {
                    try
                    {
                        _hfpDuplexCapture = new WasapiCapture(_hfpDuplexCaptureDevice, true, 50);
                        try
                        {
                            _hfpDuplexCaptureDevice.AudioSessionManager.AudioSessionControl.SetDuckingPreference(true);
                        }
                        catch { }
                        _hfpDuplexCapture.DataAvailable += (s, e) =>
                        {
                            // CRITICAL: Active buffer consumption to satisfy SCO bidirectional requirement
                            // Empty handler = passive drain, may not generate HCI-level read I/O
                            System.Threading.Interlocked.Add(ref _hfpCaptureByteCount, e.BytesRecorded);
                            
                            // Log telemetry every 2 seconds
                            var now = DateTime.UtcNow;
                            if ((now - _lastTelemetryLog).TotalSeconds >= 2.0)
                            {
                                _lastTelemetryLog = now;
                                long captureMB = _hfpCaptureByteCount / (1024 * 1024);
                                long renderMB = _hfpRenderByteCount / (1024 * 1024);
                                double ratio = _hfpRenderByteCount > 0 
                                    ? (double)_hfpCaptureByteCount / _hfpRenderByteCount 
                                    : 0.0;
                                Console.WriteLine($"[HFP-DUPLEX] Capture: {captureMB} MB | Render: {renderMB} MB | Ratio: {ratio:F2} | BytesThisPacket: {e.BytesRecorded}");
                            }
                        };
                        _hfpDuplexCapture.StartRecording();
                        Console.WriteLine($"[HFP-DUPLEX] Started capture from: {_hfpDuplexCaptureDevice.FriendlyName} ({_hfpDuplexCaptureDevice.AudioClient.MixFormat.SampleRate} Hz)");
                    }
                    catch (Exception ex)
                    {
                        DeviceWarningReceived?.Invoke(this, $"Failed to start HFP duplex capture keep-alive: {ex.Message}");
                    }
                }

                if (_wasapiCapture != null && Pipeline.IsMicEnabled)
                {
                    _wasapiCapture.StartRecording();
                }

                _wasapiOut.Play();
                _stateMachine.TransitionTo(BridgeState.Streaming);
            }
            catch
            {
                _stateMachine.TryTransitionTo(BridgeState.Error);
                throw;
            }
        }

        public void Stop()
        {
            if (_isDisposed) return;
            if (State == BridgeState.Stopped || State == BridgeState.Disconnected) return;

            try
            {
                _wasapiCapture?.StopRecording();
                _hfpDuplexCapture?.StopRecording();
                _wasapiOut?.Stop();
            }
            catch
            {
                // Suppress shutdown race conditions in WASAPI driver
            }
            finally
            {
                _hfpDuplexCapture?.Dispose();
                _hfpDuplexCapture = null;
                _stateMachine.TryTransitionTo(BridgeState.Stopped);
            }
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            Stop();
            _stateMachine.TryTransitionTo(BridgeState.Disconnected);

            _deviceWatcher.DeviceStateChanged -= OnDeviceStateChanged;
            _deviceWatcher.DeviceRemoved -= OnDeviceRemoved;
            _deviceWatcher.Dispose();
            _wasapiOut?.Dispose();
            _wasapiCapture?.Dispose();
            _hfpDuplexCapture?.Dispose();
            Pipeline.Dispose();
        }
    }
}
