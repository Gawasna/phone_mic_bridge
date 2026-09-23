using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace PhoneMicBridge.Audio
{
    public class MixingPipeline : IDisposable
    {
        private readonly MixingSampleProvider _mixer;
        private readonly int _targetSampleRate;
        private readonly int _targetChannels;

        private VolumeSampleProvider? _micVolumeProvider;
        private VolumeSampleProvider? _fileVolumeProvider;
        private AudioFileReader? _fileReader;
        private SignalGenerator? _toneGen;

        public float MicGain { get; set; } = 1.0f;
        public float FileGain { get; set; } = 0.7f;
        public float ToneGain { get; set; } = 0.35f;

        public bool IsMicEnabled { get; private set; } = true;
        public bool IsFilePlaying { get; private set; }
        public bool HasFile => _fileReader != null;
        public bool IsToneEnabled { get; private set; }
        public double ToneFrequency => _toneGen?.Frequency ?? 1000.0;

        public double FileCurrentSeconds => _fileReader?.CurrentTime.TotalSeconds ?? 0;
        public double FileTotalSeconds => _fileReader?.TotalTime.TotalSeconds ?? 0;

        public MixingPipeline(int targetSampleRate, int targetChannels = 1)
        {
            _targetSampleRate = targetSampleRate;
            _targetChannels = targetChannels;
            var format = WaveFormat.CreateIeeeFloatWaveFormat(targetSampleRate, targetChannels);
            _mixer = new MixingSampleProvider(format);
        }

        public void AttachMicSource(ISampleProvider rawMicProvider, bool initialEnabled = true)
        {
            var normalized = AudioResampler.NormalizeToDestination(rawMicProvider, _targetSampleRate, _targetChannels);
            _micVolumeProvider = new VolumeSampleProvider(normalized)
            {
                Volume = initialEnabled ? MicGain : 0.0f
            };
            IsMicEnabled = initialEnabled;
            _mixer.AddMixerInput(_micVolumeProvider);
        }

        public bool AttachFileSource(string filePath, float initialGain = 0.7f)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;

            FileGain = initialGain;
            _fileReader = new AudioFileReader(filePath) { Volume = FileGain };
            var normalized = AudioResampler.NormalizeToDestination(_fileReader, _targetSampleRate, _targetChannels);
            _fileVolumeProvider = new VolumeSampleProvider(normalized) { Volume = FileGain };

            _mixer.AddMixerInput(_fileVolumeProvider);
            IsFilePlaying = true;
            return true;
        }

        public void AttachToneSource(double frequency = 1000.0, float initialGain = 0.35f)
        {
            ToneGain = initialGain;
            _toneGen = new SignalGenerator(_targetSampleRate, _targetChannels)
            {
                Frequency = frequency,
                Gain = ToneGain,
                Type = SignalGeneratorType.Sin
            };
            _mixer.AddMixerInput(_toneGen);
        }

        public void ToggleMic()
        {
            SetMicEnabled(!IsMicEnabled);
        }

        public void SetMicEnabled(bool enabled)
        {
            IsMicEnabled = enabled;
            if (_micVolumeProvider != null)
            {
                _micVolumeProvider.Volume = enabled ? MicGain : 0.0f;
            }
        }

        public void SetMicGain(float gain)
        {
            MicGain = gain;
            if (_micVolumeProvider != null && IsMicEnabled)
            {
                _micVolumeProvider.Volume = gain;
            }
        }

        public void ToggleFile()
        {
            if (_fileVolumeProvider == null) return;
            IsFilePlaying = !IsFilePlaying;
            _fileVolumeProvider.Volume = IsFilePlaying ? FileGain : 0.0f;
        }

        public void SetFileGain(float gain)
        {
            FileGain = gain;
            if (_fileVolumeProvider != null && IsFilePlaying)
            {
                _fileVolumeProvider.Volume = gain;
            }
            if (_fileReader != null)
            {
                _fileReader.Volume = gain;
            }
        }

        public void SetToneEnabled(bool enabled, double frequency = 1000.0, float initialGain = 0.35f)
        {
            ToneGain = initialGain;
            if (_toneGen == null)
            {
                AttachToneSource(frequency, initialGain);
            }
            else
            {
                _toneGen.Frequency = frequency;
            }
            IsToneEnabled = enabled;
            if (_toneGen != null)
            {
                _toneGen.Gain = enabled ? ToneGain : 0.0;
            }
        }

        public void SetToneGain(float gain)
        {
            ToneGain = gain;
            if (_toneGen != null && IsToneEnabled)
            {
                _toneGen.Gain = gain;
            }
        }

        public ISampleProvider GetOutputSampleProvider() => _mixer;

        public void Dispose()
        {
            _fileReader?.Dispose();
            _fileReader = null;
            _fileVolumeProvider = null;
        }
    }
}
