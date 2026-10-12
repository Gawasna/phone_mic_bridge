using System;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using PhoneMicBridge.Audio;
using Xunit;

namespace PhoneMicBridge.Tests
{
    public class MixingPipelineTests
    {
        [Fact]
        public void MixingPipeline_WhenToneAttached_ShouldProduceAudioSamples()
        {
            // Arrange
            using var pipeline = new MixingPipeline(16000, 1);
            pipeline.AttachToneSource(1000.0, 0.5f);

            var outputProvider = pipeline.GetOutputSampleProvider();
            float[] buffer = new float[1600];

            // Act
            int samplesRead = outputProvider.Read(buffer.AsSpan());

            // Assert
            Assert.Equal(1600, samplesRead);

            // Find max amplitude in generated sine wave
            float max = 0;
            for (int i = 0; i < samplesRead; i++)
            {
                float abs = Math.Abs(buffer[i]);
                if (abs > max) max = abs;
            }

            Assert.True(max > 0.4f && max <= 0.6f, $"Expected tone gain ~0.5, but got max {max}");
        }

        [Fact]
        public void MixingPipeline_ToggleMic_ShouldControlMicStateAndGain()
        {
            // Arrange
            using var pipeline = new MixingPipeline(16000, 1);
            var dummyMic = new SignalGenerator(16000, 1) { Frequency = 500, Gain = 0.8 };
            pipeline.AttachMicSource(dummyMic, initialEnabled: true);

            // Assert initial state
            Assert.True(pipeline.IsMicEnabled);

            // Act: Toggle off
            pipeline.ToggleMic();
            Assert.False(pipeline.IsMicEnabled);

            // Act: Toggle on
            pipeline.ToggleMic();
            Assert.True(pipeline.IsMicEnabled);
        }
    }
}
