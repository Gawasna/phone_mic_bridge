using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.Wave;
using PhoneMicBridge.Application;
using PhoneMicBridge.Audio;
using Xunit;

namespace PhoneMicBridge.Tests
{
    public class AudioMeterStressAndPerformanceTests
    {
        private const int StressIterations = 10_000;

        #region 1. Zero-Allocation in Audio Path Verification

        [Fact]
        public void UpdateFromRawBuffer_32BitFloat_10000Iterations_AllocatesZeroBytesOnHeap()
        {
            // Arrange
            float[] sampleData = [0.1f, -0.4f, 0.9f, -0.2f, 0.05f, 0.6f, -0.8f, 0.3f];
            byte[] rawBuffer = new byte[sampleData.Length * 4];
            Buffer.BlockCopy(sampleData, 0, rawBuffer, 0, rawBuffer.Length);
            var format = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
            var meter = new AudioMeter();
            ReadOnlySpan<byte> span = rawBuffer;

            // Warm-up to complete JIT compilation and static initialization
            for (int i = 0; i < 200; i++)
            {
                meter.UpdateFromRawBuffer(span, format, 1.0f);
            }

            // Act
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < StressIterations; i++)
            {
                meter.UpdateFromRawBuffer(span, format, 1.0f);
            }
            long bytesAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocated = bytesAfter - bytesBefore;

            // Assert
            Assert.Equal(0, totalAllocated);
            Assert.Equal(0.9f, meter.Peak, precision: 3);
            Assert.True(meter.Rms > 0f);
        }

        [Fact]
        public void UpdateFromRawBuffer_16BitPcm_10000Iterations_AllocatesZeroBytesOnHeap()
        {
            // Arrange
            short[] pcmData = [1000, -5000, 16384, -24000, 0, 8000, -12000, 32000];
            byte[] rawBuffer = new byte[pcmData.Length * 2];
            Buffer.BlockCopy(pcmData, 0, rawBuffer, 0, rawBuffer.Length);
            var format = new WaveFormat(16000, 16, 1);
            var meter = new AudioMeter();
            ReadOnlySpan<byte> span = rawBuffer;

            // Warm-up
            for (int i = 0; i < 200; i++)
            {
                meter.UpdateFromRawBuffer(span, format, 1.0f);
            }

            // Act
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < StressIterations; i++)
            {
                meter.UpdateFromRawBuffer(span, format, 1.0f);
            }
            long bytesAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocated = bytesAfter - bytesBefore;

            // Assert
            Assert.Equal(0, totalAllocated);
            Assert.True(meter.Peak > 0f);
            Assert.True(meter.Rms > 0f);
        }

        [Fact]
        public void UpdateFromRawBuffer_ByteArrayOverload_10000Iterations_AllocatesZeroBytesOnHeap()
        {
            // Arrange
            float[] sampleData = [0.2f, -0.3f, 0.7f, -0.5f];
            byte[] rawBuffer = new byte[sampleData.Length * 4];
            Buffer.BlockCopy(sampleData, 0, rawBuffer, 0, rawBuffer.Length);
            var format = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
            var meter = new AudioMeter();

            // Warm-up
            for (int i = 0; i < 200; i++)
            {
                meter.UpdateFromRawBuffer(rawBuffer, rawBuffer.Length, format, 1.0f);
            }

            // Act
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < StressIterations; i++)
            {
                meter.UpdateFromRawBuffer(rawBuffer, rawBuffer.Length, format, 1.0f);
            }
            long bytesAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocated = bytesAfter - bytesBefore;

            // Assert
            Assert.Equal(0, totalAllocated);
            Assert.Equal(0.7f, meter.Peak, precision: 3);
        }

        [Fact]
        public void UpdateFromSamples_DirectSpan_10000Iterations_AllocatesZeroBytesOnHeap()
        {
            // Arrange
            float[] samples = [0.15f, -0.65f, 0.82f, -0.44f, 0.33f];
            var meter = new AudioMeter();
            ReadOnlySpan<float> span = samples;

            // Warm-up
            for (int i = 0; i < 200; i++)
            {
                meter.UpdateFromSamples(span, 1.0f);
            }

            // Act
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < StressIterations; i++)
            {
                meter.UpdateFromSamples(span, 1.0f);
            }
            long bytesAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocated = bytesAfter - bytesBefore;

            // Assert
            Assert.Equal(0, totalAllocated);
            Assert.Equal(0.82f, meter.Peak, precision: 3);
        }

        [Fact]
        public void ZeroAllocMeteringSampleProvider_Read_10000Iterations_AllocatesZeroBytesOnHeap()
        {
            // Arrange
            var dummySource = new DummySampleProvider(16000, 1, 0.65f);
            var meter = new AudioMeter();
            var provider = new ZeroAllocMeteringSampleProvider(dummySource, meter);
            float[] destinationBuffer = new float[256];
            Span<float> span = destinationBuffer;

            // Warm-up
            for (int i = 0; i < 200; i++)
            {
                provider.Read(span);
            }

            // Act
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < StressIterations; i++)
            {
                provider.Read(span);
            }
            long bytesAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocated = bytesAfter - bytesBefore;

            // Assert
            Assert.Equal(0, totalAllocated);
            Assert.Equal(0.65f, meter.Peak, precision: 3);
        }

        [Fact]
        public void ZeroAllocMeteringSampleProvider_ReadArrayOverload_10000Iterations_AllocatesZeroBytesOnHeap()
        {
            // Arrange
            var dummySource = new DummySampleProvider(16000, 1, 0.45f);
            var meter = new AudioMeter();
            var provider = new ZeroAllocMeteringSampleProvider(dummySource, meter);
            float[] destinationBuffer = new float[256];

            // Warm-up
            for (int i = 0; i < 200; i++)
            {
                provider.Read(destinationBuffer, 0, destinationBuffer.Length);
            }

            // Act
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < StressIterations; i++)
            {
                provider.Read(destinationBuffer, 0, destinationBuffer.Length);
            }
            long bytesAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocated = bytesAfter - bytesBefore;

            // Assert
            Assert.Equal(0, totalAllocated);
            Assert.Equal(0.45f, meter.Peak, precision: 3);
        }

        #endregion

        #region 2. Abnormal Signal Stress Testing (Silence, Clipping, Empty, Non-aligned)

        [Fact]
        public void EmptyBuffer_ShouldNotThrowAndRemainConsistent()
        {
            // Arrange
            var meter = new AudioMeter();
            var formatFloat = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
            var formatPcm = new WaveFormat(16000, 16, 1);

            // Act & Assert: Span overloads
            meter.UpdateFromSamples(ReadOnlySpan<float>.Empty);
            Assert.Equal(0.0f, meter.Peak);
            Assert.Equal(0.0f, meter.Rms);

            meter.UpdateFromRawBuffer(ReadOnlySpan<byte>.Empty, formatFloat);
            Assert.Equal(0.0f, meter.Peak);
            Assert.Equal(0.0f, meter.Rms);

            meter.UpdateFromRawBuffer(ReadOnlySpan<byte>.Empty, formatPcm);
            Assert.Equal(0.0f, meter.Peak);
            Assert.Equal(0.0f, meter.Rms);

            // Act & Assert: Array overloads
            meter.UpdateFromRawBuffer(new byte[0], 0, formatFloat);
            Assert.Equal(0.0f, meter.Peak);
            Assert.Equal(0.0f, meter.Rms);

            meter.UpdateFromRawBuffer(null!, 0, formatFloat);
            Assert.Equal(0.0f, meter.Peak);
            Assert.Equal(0.0f, meter.Rms);
        }

        [Fact]
        public void SilenceBuffer_ShouldProduceZeroPeakZeroRms_NoNanOrInfinities()
        {
            // Arrange
            var meter = new AudioMeter();
            byte[] silenceBytes = new byte[1024];
            var formatFloat = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);

            // Act
            meter.UpdateFromRawBuffer(silenceBytes, formatFloat);

            // Assert
            Assert.False(float.IsNaN(meter.Peak));
            Assert.False(float.IsInfinity(meter.Peak));
            Assert.False(float.IsNaN(meter.Rms));
            Assert.False(float.IsInfinity(meter.Rms));
            Assert.Equal(0.0f, meter.Peak);
            Assert.Equal(0.0f, meter.Rms);
        }

        [Theory]
        [InlineData(1.5f)]
        [InlineData(3.0f)]
        [InlineData(10.0f)]
        [InlineData(-2.5f)]
        [InlineData(-50.0f)]
        public void ClippingBuffer_ShouldCalculateAccuratePeakAndRms_NoNanOrNegativeInfinity(float extremeValue)
        {
            // Arrange
            var meter = new AudioMeter();
            float[] samples = [0.0f, extremeValue, -extremeValue, 0.1f];

            // Act
            meter.UpdateFromSamples(samples);

            // Assert
            float expectedPeak = Math.Abs(extremeValue);
            Assert.False(float.IsNaN(meter.Peak), "Peak should not be NaN");
            Assert.False(float.IsNegativeInfinity(meter.Peak), "Peak should not be -Infinity");
            Assert.False(float.IsPositiveInfinity(meter.Peak), "Peak should not be +Infinity");
            Assert.False(float.IsNaN(meter.Rms), "Rms should not be NaN");
            Assert.False(float.IsNegativeInfinity(meter.Rms), "Rms should not be -Infinity");
            Assert.False(float.IsPositiveInfinity(meter.Rms), "Rms should not be +Infinity");

            Assert.Equal(expectedPeak, meter.Peak);
            Assert.True(meter.Peak >= 1.0f, "Should indicate clipping level >= 1.0");
            Assert.True(meter.Rms > 0f, "RMS must be positive");
        }

        [Theory]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        [InlineData(5)]
        [InlineData(7)]
        public void TruncatedUnalignedBuffer_32BitFloat_ShouldHandleGracefullyWithoutCrashing(int byteLength)
        {
            // Arrange
            var meter = new AudioMeter();
            byte[] oddBuffer = new byte[byteLength];
            oddBuffer.AsSpan().Fill(0x3F); // Valid IEEE 754 bit pattern
            var format = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);

            // Act & Assert
            var ex = Record.Exception(() =>
            {
                meter.UpdateFromRawBuffer(oddBuffer, format);
            });

            Assert.Null(ex);
            Assert.False(float.IsNaN(meter.Peak));
            Assert.False(float.IsNaN(meter.Rms));
        }

        [Theory]
        [InlineData(1)]
        [InlineData(3)]
        [InlineData(5)]
        public void TruncatedUnalignedBuffer_16BitPcm_ShouldHandleGracefullyWithoutCrashing(int byteLength)
        {
            // Arrange
            var meter = new AudioMeter();
            byte[] oddBuffer = new byte[byteLength];
            oddBuffer.AsSpan().Fill(0x20);
            var format = new WaveFormat(16000, 16, 1);

            // Act & Assert
            var ex = Record.Exception(() =>
            {
                meter.UpdateFromRawBuffer(oddBuffer, format);
            });

            Assert.Null(ex);
            Assert.False(float.IsNaN(meter.Peak));
            Assert.False(float.IsNaN(meter.Rms));
        }

        [Fact]
        public void ConcurrentAudioProcessingAndUiReading_ShouldBeThreadSafeAndNotCrash()
        {
            // Arrange
            var meter = new AudioMeter();
            var format = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);
            float[] sampleData = [0.1f, -0.4f, 0.9f, -0.2f];
            byte[] rawBuffer = new byte[sampleData.Length * 4];
            Buffer.BlockCopy(sampleData, 0, rawBuffer, 0, rawBuffer.Length);

            bool isRunning = true;
            Exception? backgroundException = null;

            // Background audio thread simulating 100Hz audio buffer callbacks
            var audioThread = new System.Threading.Thread(() =>
            {
                try
                {
                    for (int i = 0; i < 20_000 && isRunning; i++)
                    {
                        meter.UpdateFromRawBuffer(rawBuffer, format, 1.0f);
                    }
                }
                catch (Exception ex)
                {
                    backgroundException = ex;
                }
            });

            audioThread.Start();

            // Foreground UI thread simulating 30Hz timer polling
            for (int i = 0; i < 20_000; i++)
            {
                float peak = meter.Peak;
                float rms = meter.Rms;

                Assert.False(float.IsNaN(peak));
                Assert.False(float.IsNaN(rms));
            }

            isRunning = false;
            audioThread.Join(TimeSpan.FromSeconds(5));

            Assert.Null(backgroundException);
        }

        [Theory]
        [InlineData(0.0f, -60.0f)]
        [InlineData(-1.0f, -60.0f)]
        [InlineData(0.00005f, -60.0f)]
        [InlineData(0.0001f, -60.0f)]
        [InlineData(1.0f, 0.0f)]
        [InlineData(2.0f, 6.0206f)]
        [InlineData(10.0f, 20.0f)]
        public void AmplitudeToDb_ShouldHandleSilenceClippingAndNeverProduceNegativeInfinityOrNan(float amplitude, float expectedDb)
        {
            // Arrange: inspect private static AmplitudeToDb method on BridgeApplicationService
            var method = typeof(BridgeApplicationService).GetMethod(
                "AmplitudeToDb",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);

            Assert.NotNull(method);

            // Act
            float result = (float)method.Invoke(null, [amplitude, -60.0f])!;

            // Assert
            Assert.False(float.IsNaN(result), "Result must not be NaN");
            Assert.False(float.IsNegativeInfinity(result), "Result must not be -Infinity");
            Assert.False(float.IsPositiveInfinity(result), "Result must not be +Infinity");
            Assert.Equal(expectedDb, result, precision: 2);
        }

        #endregion

        #region 3. UI DispatcherTimer (30Hz) Simulation & Latency Benchmark

        [Fact]
        public void PollingCurrentMeterValues_10000Iterations_AllocatesZeroBytesOnHeap()
        {
            // Arrange
            using var service = new BridgeApplicationService();

            // Warm-up
            for (int i = 0; i < 200; i++)
            {
                _ = service.GetCurrentMeterValues();
            }

            // Act: Simulate 10,000 UI DispatcherTimer ticks (approx 5.5 minutes of continuous 30Hz polling)
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < StressIterations; i++)
            {
                AudioMeterValues values = service.GetCurrentMeterValues();
                // Access all fields to verify no boxing or hidden conversions
                _ = values.InputPeak;
                _ = values.InputRms;
                _ = values.InputPeakDb;
                _ = values.OutputPeak;
                _ = values.OutputRms;
                _ = values.OutputPeakDb;
                _ = values.IsInputClipping;
                _ = values.IsOutputClipping;
            }
            long bytesAfter = GC.GetAllocatedBytesForCurrentThread();
            long totalAllocated = bytesAfter - bytesBefore;

            // Assert
            Assert.Equal(0, totalAllocated);
        }

        [Fact]
        public void AudioMeterValues_ReadLatency_DoesNotBlockUIThread()
        {
            // Arrange
            using var service = new BridgeApplicationService();
            var sw = Stopwatch.StartNew();

            // Act: 10,000 polls
            for (int i = 0; i < StressIterations; i++)
            {
                AudioMeterValues values = service.GetCurrentMeterValues();
                if (values.IsInputClipping) { /* no-op */ }
            }
            sw.Stop();

            double totalMs = sw.Elapsed.TotalMilliseconds;
            double avgMicrosecondsPerTick = (totalMs / StressIterations) * 1000.0;

            // Assert: Total execution for 10,000 ticks should easily finish under 50ms,
            // meaning average tick latency is < 5 microseconds (a fraction of 1% of the 33ms 30Hz frame budget).
            Assert.True(totalMs < 50.0, $"10,000 ticks took {totalMs:F2}ms, which exceeded 50ms threshold");
            Assert.True(avgMicrosecondsPerTick < 10.0, $"Average latency {avgMicrosecondsPerTick:F2}us exceeded 10us");
        }

        [Fact]
        public void AudioMeterValues_StructCopy_DoesNotAllocateOnHeap()
        {
            // Arrange
            var original = new AudioMeterValues(
                InputPeak: 0.85f,
                InputRms: 0.60f,
                InputPeakDb: -1.41f,
                OutputPeak: 0.95f,
                OutputRms: 0.70f,
                OutputPeakDb: -0.45f,
                IsInputClipping: false,
                IsOutputClipping: false
            );

            // Warm-up
            AudioMeterValues copy = default;
            for (int i = 0; i < 200; i++)
            {
                copy = original;
            }

            // Act
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < StressIterations; i++)
            {
                copy = original;
                if (copy.InputPeak < 0f) break;
            }
            long bytesAfter = GC.GetAllocatedBytesForCurrentThread();

            // Assert
            Assert.Equal(0, bytesAfter - bytesBefore);
            Assert.Equal(0.85f, copy.InputPeak);
        }

        #endregion
    }
}
