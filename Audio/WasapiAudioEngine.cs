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
        private WasapiOut? _wasapiOut;
        private WasapiCapture? _wasapiCapture;
        private BufferedWaveProvider? _captureBuffer;

        public BridgeState State { get; private set; } = BridgeState.Disconnected;
        public event EventHandler<BridgeState>? StateChanged;

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
        }

        public void SetupMicCapture(bool initialEnabled = true)
        {
            if (_captureDevice == null) return;

            _wasapiCapture = new WasapiCapture(_captureDevice, true, 50);
            _captureBuffer = new BufferedWaveProvider(_wasapiCapture.WaveFormat)
            {
                DiscardOnBufferOverflow = true
            };

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
            if (State == BridgeState.Streaming) return;

            SetState(BridgeState.Connecting);

            var outputProvider = OutputMeter.Wrap(Pipeline.GetOutputSampleProvider());

            _wasapiOut = new WasapiOut(_renderDevice, AudioClientShareMode.Shared, false, 50);
            _wasapiOut.Init(outputProvider);

            if (_wasapiCapture != null && Pipeline.IsMicEnabled)
            {
                _wasapiCapture.StartRecording();
            }

            _wasapiOut.Play();
            SetState(BridgeState.Streaming);
        }

        public void Stop()
        {
            if (State == BridgeState.Stopped || State == BridgeState.Disconnected) return;

            try
            {
                _wasapiCapture?.StopRecording();
                _wasapiOut?.Stop();
            }
            catch
            {
                // Suppress shutdown race conditions in WASAPI driver
            }
            finally
            {
                SetState(BridgeState.Stopped);
            }
        }

        private void SetState(BridgeState newState)
        {
            State = newState;
            StateChanged?.Invoke(this, newState);
        }

        public void Dispose()
        {
            Stop();
            _wasapiOut?.Dispose();
            _wasapiCapture?.Dispose();
            Pipeline.Dispose();
        }
    }
}
