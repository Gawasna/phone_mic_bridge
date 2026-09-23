using System;
using NAudio.Wave;
using PhoneMicBridge.Audio;
using Xunit;

namespace PhoneMicBridge.Tests
{
    public class AudioMeterTests
    {
        [Fact]
        public void UpdateFromRawBuffer_With32BitFloat_ShouldCalculateAccuratePeakAndRms()
        {
            // Arrange: 4 samples with values [0.0, 0.5, -0.5, 0.0]
            float[] samples = [0.0f, 0.5f, -0.5f, 0.0f];
            byte[] buffer = new byte[samples.Length * 4];
            Buffer.BlockCopy(samples, 0, buffer, 0, buffer.Length);

            var format = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
            var meter = new AudioMeter();

            // Act
            meter.UpdateFromRawBuffer(buffer, buffer.Length, format, gain: 1.0f);

            // Assert
            Assert.Equal(0.5f, meter.Peak, precision: 3);
            // RMS = sqrt((0^2 + 0.5^2 + (-0.5)^2 + 0^2) / 4) = sqrt(0.5 / 4) = sqrt(0.125) = ~0.3535
            Assert.Equal(0.354f, meter.Rms, precision: 3);
        }

        [Fact]
        public void UpdateFromRawBuffer_WithSilence_ShouldReturnZeroPeakAndRms()
        {
            // Arrange
            byte[] silenceBuffer = new byte[64];
            var format = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
            var meter = new AudioMeter();

            // Act
            meter.UpdateFromRawBuffer(silenceBuffer, silenceBuffer.Length, format, gain: 1.0f);

            // Assert
            Assert.Equal(0.0f, meter.Peak);
            Assert.Equal(0.0f, meter.Rms);
        }

        [Fact]
        public void UpdateFromSamples_DirectSpan_ShouldCalculateAccuratePeakAndRmsWithoutAllocations()
        {
            // Arrange
            float[] sampleArray = [0.0f, 0.8f, -0.6f, 0.2f];
            var meter = new AudioMeter();

            // Act: measure allocations in hot path
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            meter.UpdateFromSamples(sampleArray.AsSpan());
            long bytesAfter = GC.GetAllocatedBytesForCurrentThread();

            // Assert
            Assert.Equal(0.8f, meter.Peak, precision: 3);
            Assert.Equal(0, bytesAfter - bytesBefore);
        }

        [Fact]
        public void ZeroAllocMeteringSampleProvider_WhenRead_UpdatesMeterWithoutAllocations()
        {
            // Arrange
            var source = new DummySampleProvider(16000, 1, 0.75f);
            var meter = new AudioMeter();
            var provider = new PhoneMicBridge.Application.ZeroAllocMeteringSampleProvider(source, meter);
            float[] buffer = new float[256];

            // Act
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            int read = provider.Read(buffer.AsSpan());
            long bytesAfter = GC.GetAllocatedBytesForCurrentThread();

            // Assert
            Assert.Equal(256, read);
            Assert.Equal(0.75f, meter.Peak, precision: 3);
            Assert.Equal(0, bytesAfter - bytesBefore);
        }
    }

    internal class DummySampleProvider : ISampleProvider
    {
        private readonly float _constantValue;
        public WaveFormat WaveFormat { get; }

        public DummySampleProvider(int sampleRate, int channels, float constantValue)
        {
            WaveFormat = WaveFormat.CreateIeeeFloatWaveFormat(sampleRate, channels);
            _constantValue = constantValue;
        }

        public int Read(Span<float> buffer)
        {
            buffer.Fill(_constantValue);
            return buffer.Length;
        }

        public int Read(float[] buffer, int offset, int count)
        {
            return Read(buffer.AsSpan(offset, count));
        }
    }
}
