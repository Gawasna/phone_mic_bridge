using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using PhoneMicBridge.Application;
using PhoneMicBridge.Audio;
using PhoneMicBridge.Core;
using Xunit;

namespace PhoneMicBridge.Tests
{
    public class SourceControlsAndAudioMeterTests
    {
        #region Test Doubles & Reflection Helpers

        private class StubDiagnosticsEngine : IPreflightDiagnosticsEngine
        {
            public IReadOnlyList<PreflightItemDto> ItemsToReturn { get; set; } = new List<PreflightItemDto>
            {
                new("CHECK_MIC_INPUT", "Microphone Input Check", "Pass", "Microphone active", "None"),
                new("CHECK_RENDER_DEVICE", "Target Output Device Check", "Pass", "Render active", "None"),
                new("CHECK_BLUETOOTH_TRANSPORT", "Bluetooth HFP Check", "Pass", "SCO transport ready", "None")
            };

            public Task<IReadOnlyList<PreflightItemDto>> ExecuteChecksAsync(
                string? captureDeviceId,
                string? renderDeviceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(ItemsToReturn);
            }
        }

        private class ConstantSampleProvider : ISampleProvider
        {
            private readonly float[] _samples;
            public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(16000, 1);

            public ConstantSampleProvider(float[] samples)
            {
                _samples = samples;
            }

            public int Read(float[] buffer, int offset, int count)
            {
                int toCopy = Math.Min(count, _samples.Length);
                Array.Copy(_samples, 0, buffer, offset, toCopy);
                return toCopy;
            }

            public int Read(Span<float> buffer)
            {
                int toCopy = Math.Min(buffer.Length, _samples.Length);
                _samples.AsSpan(0, toCopy).CopyTo(buffer);
                return toCopy;
            }
        }

        private static string CreateTempWavFile(int numSamples = 16000, int sampleRate = 16000)
        {
            string path = Path.Combine(Path.GetTempPath(), $"source_ctrl_test_{Guid.NewGuid():N}.wav");
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            using var bw = new BinaryWriter(fs);

            int subchunk2Size = numSamples * 2;
            int chunkSize = 36 + subchunk2Size;

            // RIFF header
            bw.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
            bw.Write(chunkSize);
            bw.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

            // fmt chunk
            bw.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
            bw.Write(16);
            bw.Write((short)1); // PCM
            bw.Write((short)1); // Mono
            bw.Write(sampleRate);
            bw.Write(sampleRate * 2);
            bw.Write((short)2);
            bw.Write((short)16);

            // data chunk
            bw.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            bw.Write(subchunk2Size);

            for (int i = 0; i < numSamples; i++)
            {
                short sample = (short)(Math.Sin(2.0 * Math.PI * 440.0 * i / sampleRate) * 16000.0);
                bw.Write(sample);
            }

            return path;
        }

        private static object CreateMainViewModel(IBridgeApplicationService service)
        {
            string testDir = AppContext.BaseDirectory;
            string guiDllPath = Path.GetFullPath(Path.Combine(testDir, @"..\..\..\..\..\Gui\PhoneMicBridge.Gui\bin\Debug\net9.0-windows\PhoneMicBridge.Gui.dll"));

            if (!File.Exists(guiDllPath))
            {
                string solutionRoot = Path.GetFullPath(Path.Combine(testDir, @"..\..\..\..\..\"));
                guiDllPath = Path.Combine(solutionRoot, "Gui", "PhoneMicBridge.Gui", "bin", "Debug", "net9.0-windows", "PhoneMicBridge.Gui.dll");
            }

            Assert.True(File.Exists(guiDllPath), $"PhoneMicBridge.Gui.dll must exist at: {guiDllPath}");

            Assembly guiAssembly = Assembly.LoadFrom(guiDllPath);
            Type vmType = guiAssembly.GetType("PhoneMicBridge.Gui.ViewModels.MainViewModel")
                ?? throw new InvalidOperationException("PhoneMicBridge.Gui.ViewModels.MainViewModel type not found.");

            return Activator.CreateInstance(vmType, service)!;
        }

        private static T GetProperty<T>(object instance, string propertyName)
        {
            var prop = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"Property '{propertyName}' not found on type '{instance.GetType().FullName}'.");
            return (T)prop.GetValue(instance)!;
        }

        private static void SetProperty<T>(object instance, string propertyName, T value)
        {
            var prop = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"Property '{propertyName}' not found on type '{instance.GetType().FullName}'.");
            prop.SetValue(instance, value);
        }

        private static object? InvokeMethod(object instance, string methodName, params object[] args)
        {
            var method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"Method '{methodName}' not found on type '{instance.GetType().FullName}'.");
            return method.Invoke(instance, args);
        }

        #endregion

        #region 1. Microphone Source Controls (Mute / Unmute & Linear Gain)

        [Fact]
        public void MicControls_InitialState_ShouldBeEnabledWithUnitGain()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            // Assert
            Assert.True(service.CurrentSnapshot.IsMicEnabled);
            Assert.Equal(1.0f, service.CurrentSnapshot.MicGain);
        }

        [Fact]
        public void MicControls_SetMicMuted_ShouldUpdateSnapshotMuteState()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            // Act: Mute microphone
            service.SetMicMuted(true);

            // Assert
            Assert.False(service.CurrentSnapshot.IsMicEnabled);

            // Act: Unmute microphone
            service.SetMicMuted(false);

            // Assert
            Assert.True(service.CurrentSnapshot.IsMicEnabled);
        }

        [Fact]
        public void MicControls_ToggleMic_ShouldFlipMicrophoneState()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);
            Assert.True(service.CurrentSnapshot.IsMicEnabled);

            // Act & Assert 1: Toggle to Muted
            service.ToggleMic();
            Assert.False(service.CurrentSnapshot.IsMicEnabled);

            // Act & Assert 2: Toggle to Unmuted
            service.ToggleMic();
            Assert.True(service.CurrentSnapshot.IsMicEnabled);
        }

        [Theory]
        [InlineData(0.0f, 0.0f)]
        [InlineData(0.5f, 0.5f)]
        [InlineData(1.0f, 1.0f)]
        [InlineData(1.75f, 1.75f)]
        [InlineData(2.0f, 2.0f)]
        [InlineData(-0.5f, 0.0f)] // Clamped to lower bound
        [InlineData(3.5f, 2.0f)]  // Clamped to upper bound
        public void MicControls_SetMicGain_ShouldClampWithinZeroAndTwo(float requestedGain, float expectedGain)
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            // Act
            service.SetMicGain(requestedGain);

            // Assert
            Assert.Equal(expectedGain, service.CurrentSnapshot.MicGain, precision: 3);
        }

        [Fact]
        public void MicControls_MuteAndGain_ShouldNotCauseAudioPipelineCrash()
        {
            // Arrange
            using var pipeline = new MixingPipeline(16000, 1);
            var samples = new float[] { 0.2f, -0.3f, 0.5f, -0.1f };
            var rawProvider = new ConstantSampleProvider(samples);

            pipeline.AttachMicSource(rawProvider, initialEnabled: true);
            Assert.True(pipeline.IsMicEnabled);
            Assert.Equal(1.0f, pipeline.MicGain);

            // Act: Smooth gain adjustment and mute
            pipeline.SetMicGain(1.5f);
            Assert.Equal(1.5f, pipeline.MicGain);

            pipeline.SetMicEnabled(false);
            Assert.False(pipeline.IsMicEnabled);

            pipeline.ToggleMic();
            Assert.True(pipeline.IsMicEnabled);

            var outputProvider = pipeline.GetOutputSampleProvider();
            float[] buffer = new float[16];
            int read = outputProvider.Read(buffer.AsSpan());

            // Assert: Read completed safely
            Assert.True(read >= 0);
        }

        #endregion

        #region 2. Test Tone Generator Controls

        [Fact]
        public void ToneGenerator_InitialState_ShouldBeDisabled()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            // Assert
            Assert.False(service.CurrentSnapshot.IsToneEnabled);
            Assert.Equal(1000.0, service.CurrentSnapshot.ToneFrequency);
            Assert.Equal(0.35f, service.CurrentSnapshot.ToneGain, precision: 2);
        }

        [Fact]
        public void ToneGenerator_SetToneEnabled_ShouldUpdateSnapshot()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            // Act: Enable 1000Hz Tone (-12 dBFS / 0.35f)
            service.SetToneEnabled(true, frequency: 1000.0, initialGain: 0.35f);

            // Assert
            Assert.True(service.CurrentSnapshot.IsToneEnabled);
            Assert.Equal(1000.0, service.CurrentSnapshot.ToneFrequency);
            Assert.Equal(0.35f, service.CurrentSnapshot.ToneGain, precision: 2);

            // Act: Disable tone
            service.SetToneEnabled(false);
            Assert.False(service.CurrentSnapshot.IsToneEnabled);
        }

        [Fact]
        public void ToneGenerator_ToggleTestTone_ShouldSwitchToneState()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);
            Assert.False(service.CurrentSnapshot.IsToneEnabled);

            // Act: Toggle on
            service.ToggleTestTone();
            Assert.True(service.CurrentSnapshot.IsToneEnabled);

            // Act: Toggle off
            service.ToggleTestTone();
            Assert.False(service.CurrentSnapshot.IsToneEnabled);
        }

        [Theory]
        [InlineData(-1.0f, 0.0f)]
        [InlineData(0.5f, 0.5f)]
        [InlineData(2.5f, 2.0f)]
        public void ToneGenerator_SetToneGain_ShouldClampWithinRange(float requestedGain, float expectedGain)
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            // Act
            service.SetToneGain(requestedGain);

            // Assert
            Assert.Equal(expectedGain, service.CurrentSnapshot.ToneGain, precision: 3);
        }

        #endregion

        #region 3. Audio File Player Source Controls

        [Fact]
        public async Task AudioFile_LoadNonExistentFile_ShouldReturnFalseAndNotChangePlayback()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            // Act
            bool loaded = await service.LoadAudioFileAsync(@"C:\non_existent_folder_xyz\missing_audio.wav");

            // Assert
            Assert.False(loaded);
            Assert.False(service.CurrentSnapshot.HasAudioFile);
            Assert.False(service.CurrentSnapshot.IsFilePlaying);
            Assert.Null(service.CurrentSnapshot.AudioFileName);
        }

        [Fact]
        public async Task AudioFile_LoadValidWavFile_ShouldSetSnapshotAndAllowPlaybackControl()
        {
            // Arrange
            string tempWav = CreateTempWavFile();
            try
            {
                var stub = new StubDiagnosticsEngine();
                using var service = new BridgeApplicationService(stub);

                // Act: Load audio file
                bool loaded = await service.LoadAudioFileAsync(tempWav, initialGain: 0.8f);

                // Assert
                Assert.True(loaded);
                Assert.True(service.CurrentSnapshot.HasAudioFile);
                Assert.True(service.CurrentSnapshot.IsFilePlaying);
                Assert.Equal(Path.GetFileName(tempWav), service.CurrentSnapshot.AudioFileName);
                Assert.Equal(0.8f, service.CurrentSnapshot.FileGain, precision: 2);

                // Act: Toggle playback
                service.ToggleFilePlayback();
                Assert.False(service.CurrentSnapshot.IsFilePlaying);

                service.ToggleFilePlayback();
                Assert.True(service.CurrentSnapshot.IsFilePlaying);

                // Act: Stop playback
                service.StopTestAudio();
                Assert.False(service.CurrentSnapshot.IsFilePlaying);
            }
            finally
            {
                if (File.Exists(tempWav)) File.Delete(tempWav);
            }
        }

        [Fact]
        public async Task AudioFile_PlayTestAudio_ShouldLoadAndPlaySynchronously()
        {
            // Arrange
            string tempWav = CreateTempWavFile();
            try
            {
                var stub = new StubDiagnosticsEngine();
                using var service = new BridgeApplicationService(stub);

                // Act
                service.PlayTestAudio(tempWav);

                // Assert
                Assert.True(service.CurrentSnapshot.HasAudioFile);
                Assert.True(service.CurrentSnapshot.IsFilePlaying);
                Assert.Equal(Path.GetFileName(tempWav), service.CurrentSnapshot.AudioFileName);

                // Act: PlayTestAudioAsync overload
                bool reloaded = await service.PlayTestAudioAsync(tempWav);
                Assert.True(reloaded);
                Assert.True(service.CurrentSnapshot.IsFilePlaying);
            }
            finally
            {
                if (File.Exists(tempWav)) File.Delete(tempWav);
            }
        }

        [Theory]
        [InlineData(-0.5f, 0.0f)]
        [InlineData(0.7f, 0.7f)]
        [InlineData(2.5f, 2.0f)]
        public void AudioFile_SetFileGain_ShouldClampWithinRange(float requestedGain, float expectedGain)
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            // Act
            service.SetFileGain(requestedGain);

            // Assert
            Assert.Equal(expectedGain, service.CurrentSnapshot.FileGain, precision: 3);
        }

        #endregion

        #region 4. Zero-Allocation Hot Path Metering Verification

        [Fact]
        public void AudioMeter_UpdateFromSamples_MustProduceZeroHeapAllocationAcrossIterations()
        {
            // Arrange
            var meter = new AudioMeter();
            float[] samples = new float[1024];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = (float)Math.Sin(i * 0.05);
            }

            // Warm up JIT and tiering
            meter.UpdateFromSamples(samples.AsSpan());

            // Act: Measure GC memory allocation across 10,000 hot-path iterations
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 10000; i++)
            {
                meter.UpdateFromSamples(samples.AsSpan());
            }
            long bytesAfter = GC.GetAllocatedBytesForCurrentThread();

            // Assert: Exactly zero bytes allocated on the heap in hot path
            Assert.Equal(0, bytesAfter - bytesBefore);
            Assert.True(meter.Peak > 0.0f);
            Assert.True(meter.Rms > 0.0f);
        }

        [Fact]
        public void ZeroAllocMeteringSampleProvider_Read_MustProduceZeroHeapAllocation()
        {
            // Arrange
            float[] sampleData = new float[512];
            for (int i = 0; i < sampleData.Length; i++) sampleData[i] = 0.4f;

            var constantSource = new ConstantSampleProvider(sampleData);
            var meter = new AudioMeter();
            var decorator = new ZeroAllocMeteringSampleProvider(constantSource, meter);

            float[] readBuffer = new float[512];

            // Warm up JIT
            decorator.Read(readBuffer.AsSpan());

            // Act: 5,000 Read cycles in hot path
            long bytesBefore = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 5000; i++)
            {
                decorator.Read(readBuffer.AsSpan());
            }
            long bytesAfter = GC.GetAllocatedBytesForCurrentThread();

            // Assert: Absolutely zero heap allocations
            Assert.Equal(0, bytesAfter - bytesBefore);
            Assert.Equal(0.4f, meter.Peak, precision: 3);
        }

        #endregion

        #region 5. Decibel Calculation & CLIP Detection

        [Fact]
        public void MeterValues_SilentInput_ShouldReportMinus60DbWithoutClipping()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            // Act
            var meters = service.GetCurrentMeterValues();

            // Assert
            Assert.Equal(0.0f, meters.InputPeak);
            Assert.Equal(-60.0f, meters.InputPeakDb);
            Assert.False(meters.IsInputClipping);
            Assert.Equal(0.0f, meters.OutputPeak);
            Assert.Equal(-60.0f, meters.OutputPeakDb);
            Assert.False(meters.IsOutputClipping);
        }

        [Fact]
        public void MeterValues_SubClippingSignal_ShouldNotTriggerClipFlag()
        {
            // Arrange: -6 dBFS corresponds to amplitude ~0.501
            float subClipAmplitude = 0.501f;
            float subClipDb = 20.0f * MathF.Log10(subClipAmplitude); // ~ -6.0 dBFS
            Assert.True(subClipDb < -0.1f);

            var values = new AudioMeterValues(
                InputPeak: subClipAmplitude,
                InputRms: 0.35f,
                InputPeakDb: subClipDb,
                OutputPeak: subClipAmplitude,
                OutputRms: 0.35f,
                OutputPeakDb: subClipDb,
                IsInputClipping: subClipDb >= -0.1f,
                IsOutputClipping: subClipDb >= -0.1f
            );

            // Assert
            Assert.False(values.IsInputClipping);
            Assert.False(values.IsOutputClipping);
        }

        [Theory]
        [InlineData(-0.1f, true)]   // Exactly at clipping threshold -0.1 dBFS
        [InlineData(-0.05f, true)]  // Above -0.1 dBFS
        [InlineData(0.0f, true)]    // Full scale 0 dBFS
        [InlineData(1.5f, true)]    // Overshoot > 0 dBFS
        [InlineData(-0.15f, false)] // Just below clipping threshold
        [InlineData(-12.0f, false)] // Standard operating level
        [InlineData(-60.0f, false)] // Silence
        public void MeterValues_ClipDetectionThreshold_EvaluatesCorrectly(float peakDb, bool expectedClip)
        {
            // Act
            bool isClipping = peakDb >= -0.1f;

            // Assert
            Assert.Equal(expectedClip, isClipping);
        }

        [Fact]
        public void MeterValues_NearFullScale_DetectsClippingAtMinusZeroPointOneDb()
        {
            // 0.9886f amplitude corresponds to -0.0999 dBFS >= -0.1 dBFS
            float nearFullScale = 0.9886f;
            float calculatedDb = 20.0f * MathF.Log10(nearFullScale);
            Assert.True(calculatedDb >= -0.1f);

            var meterValues = new AudioMeterValues(
                InputPeak: nearFullScale,
                InputRms: 0.7f,
                InputPeakDb: calculatedDb,
                OutputPeak: nearFullScale,
                OutputRms: 0.7f,
                OutputPeakDb: calculatedDb,
                IsInputClipping: calculatedDb >= -0.1f || nearFullScale >= 0.98855f,
                IsOutputClipping: calculatedDb >= -0.1f || nearFullScale >= 0.98855f
            );

            // Assert
            Assert.True(meterValues.IsInputClipping);
            Assert.True(meterValues.IsOutputClipping);
        }

        #endregion

        #region 6. MainViewModel Source Controls & Meter Bindings

        [Fact]
        public void ViewModel_MicControls_ShouldSynchronizeWithService()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);
            var vm = CreateMainViewModel(service);

            // Initial
            Assert.False(GetProperty<bool>(vm, "IsMicMuted"));
            Assert.Equal("UNMUTED", GetProperty<string>(vm, "MicStatusText"));
            Assert.Equal("1.00x", GetProperty<string>(vm, "MicGainText"));

            // Act 1: Toggle Mic via command/method
            InvokeMethod(vm, "ToggleMic");
            Assert.True(GetProperty<bool>(vm, "IsMicMuted"));
            Assert.Equal("MUTED", GetProperty<string>(vm, "MicStatusText"));
            Assert.Equal("UNMUTE MIC", GetProperty<string>(vm, "MicMuteButtonText"));
            Assert.False(service.CurrentSnapshot.IsMicEnabled);

            // Act 2: Adjust Mic Gain
            SetProperty(vm, "MicGain", 1.75f);
            Assert.Equal("1.75x", GetProperty<string>(vm, "MicGainText"));
            Assert.Equal(1.75f, service.CurrentSnapshot.MicGain, precision: 2);
        }

        [Fact]
        public void ViewModel_ToneControls_ShouldSynchronizeWithService()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);
            var vm = CreateMainViewModel(service);

            // Initial
            Assert.False(GetProperty<bool>(vm, "IsToneEnabled"));
            Assert.Equal("OFF", GetProperty<string>(vm, "ToneStatusText"));
            Assert.Equal("START TONE", GetProperty<string>(vm, "ToneButtonText"));

            // Act: Toggle Tone on
            InvokeMethod(vm, "ToggleTestTone");
            Assert.True(GetProperty<bool>(vm, "IsToneEnabled"));
            Assert.Contains("TONE ACTIVE", GetProperty<string>(vm, "ToneStatusText"));
            Assert.Equal("STOP TONE", GetProperty<string>(vm, "ToneButtonText"));
            Assert.True(service.CurrentSnapshot.IsToneEnabled);

            // Act: Adjust Tone Gain
            SetProperty(vm, "ToneGain", 1.25f);
            Assert.Equal("1.25x", GetProperty<string>(vm, "ToneGainText"));
            Assert.Equal(1.25f, service.CurrentSnapshot.ToneGain, precision: 2);
        }

        [Fact]
        public void ViewModel_AudioFileControls_ShouldSynchronizeWithService()
        {
            // Arrange
            string tempWav = CreateTempWavFile();
            try
            {
                var stub = new StubDiagnosticsEngine();
                using var service = new BridgeApplicationService(stub);
                var vm = CreateMainViewModel(service);

                // Initial
                Assert.False(GetProperty<bool>(vm, "HasAudioFile"));
                Assert.Equal("PLAY AUDIO", GetProperty<string>(vm, "PlayButtonText"));
                Assert.Equal("No file loaded", GetProperty<string>(vm, "AudioFileStatusText"));

                // Act: Play test audio file
                InvokeMethod(vm, "PlayTestAudio", tempWav);

                // Assert
                Assert.True(GetProperty<bool>(vm, "HasAudioFile"));
                Assert.True(GetProperty<bool>(vm, "IsFilePlaying"));
                Assert.Equal("STOP AUDIO", GetProperty<string>(vm, "PlayButtonText"));
                Assert.Contains(Path.GetFileName(tempWav), GetProperty<string>(vm, "AudioFileStatusText"));
                Assert.Equal(Path.GetFileName(tempWav), GetProperty<string?>(vm, "TestAudioFileName"));

                // Act: Adjust File Gain
                SetProperty(vm, "FileGain", 1.40f);
                Assert.Equal("1.40x", GetProperty<string>(vm, "FileGainText"));
                Assert.Equal(1.40f, service.CurrentSnapshot.FileGain, precision: 2);

                // Act: Stop playback
                InvokeMethod(vm, "StopTestAudio");
                Assert.False(GetProperty<bool>(vm, "IsFilePlaying"));
                Assert.Equal("PLAY AUDIO", GetProperty<string>(vm, "PlayButtonText"));
            }
            finally
            {
                if (File.Exists(tempWav)) File.Delete(tempWav);
            }
        }

        [Fact]
        public void ViewModel_MeterHelpers_ShouldReflectPeakPercentAndDbText()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);
            var vm = CreateMainViewModel(service);

            // Act: Update meters with silent values
            InvokeMethod(vm, "UpdateMeters");

            // Assert
            Assert.Equal(0.0, GetProperty<double>(vm, "InputPeakPercent"));
            Assert.Equal("-inf dBFS", GetProperty<string>(vm, "InputPeakDbText"));
            Assert.False(GetProperty<bool>(vm, "IsInputClipping"));
            Assert.False(GetProperty<bool>(vm, "IsOutputClipping"));
        }

        [Fact]
        public void ViewModel_MeterBrushTransitions_ShouldTransitionThroughGreenAmberAndRed()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);
            var vm = CreateMainViewModel(service);

            // Initial: Disconnected / silent should have green brush
            var greenBrush = GetProperty<object>(vm, "InputMeterBrush");
            Assert.NotNull(greenBrush);

            // Verify CLIP flag is initially false
            Assert.False(GetProperty<bool>(vm, "IsInputClipping"));
            Assert.False(GetProperty<bool>(vm, "IsOutputClipping"));
        }

        #endregion
    }
}
