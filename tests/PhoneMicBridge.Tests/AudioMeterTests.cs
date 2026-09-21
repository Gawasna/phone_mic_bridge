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
    }
}
