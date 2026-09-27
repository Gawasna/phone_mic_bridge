using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PhoneMicBridge.Audio;
using Xunit;

namespace PhoneMicBridge.Tests
{
    public class AudioResamplerTests
    {
        [Fact]
        public void NormalizeToDestination_ShouldDownmixStereoToMonoAndResample()
        {
            // Arrange: 44.1kHz Stereo sine wave
            var source = new SignalGenerator(44100, 2)
            {
                Frequency = 1000,
                Gain = 0.5
            };

            // Act: Target 16.000 Hz, 1 channel (Bluetooth HFP standard)
            var normalized = AudioResampler.NormalizeToDestination(source, 16000, 1);

            // Assert
            Assert.Equal(16000, normalized.WaveFormat.SampleRate);
            Assert.Equal(1, normalized.WaveFormat.Channels);

            // Verify samples can be read without crashing
            float[] buffer = new float[1600]; // 100ms
            int read = normalized.Read(buffer.AsSpan());
            Assert.True(read > 0);
        }

        [Fact]
        public void NormalizeToDestination_WhenAlreadyTargetFormat_ShouldPreserveParameters()
        {
            // Arrange: 16kHz Mono
            var source = new SignalGenerator(16000, 1)
            {
                Frequency = 800,
                Gain = 0.4
            };

            // Act
            var normalized = AudioResampler.NormalizeToDestination(source, 16000, 1);

            // Assert
            Assert.Equal(16000, normalized.WaveFormat.SampleRate);
            Assert.Equal(1, normalized.WaveFormat.Channels);
        }

        [Fact]
        public void NormalizeToDestination_ExtensibleFormat_ShouldWork()
        {
            var format = new WaveFormatExtensible(48000, 32, 2);
            var bwp = new BufferedWaveProvider(format) { ReadFully = true };
            float[] sampleData = new float[960];
            for (int i = 0; i < sampleData.Length; i++) sampleData[i] = 0.5f;
            byte[] byteData = new byte[sampleData.Length * 4];
            Buffer.BlockCopy(sampleData, 0, byteData, 0, byteData.Length);
            bwp.AddSamples(byteData, 0, byteData.Length);

            var sp = bwp.ToSampleProvider();
            var normalized = AudioResampler.NormalizeToDestination(sp, 16000, 1);
            float[] outBuffer = new float[320];
            int read = normalized.Read(outBuffer.AsSpan());
            Assert.True(read > 0, $"Expected read > 0, got {read}");
        }
    }
}
