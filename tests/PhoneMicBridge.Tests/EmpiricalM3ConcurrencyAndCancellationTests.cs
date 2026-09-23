using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PhoneMicBridge.Application;
using PhoneMicBridge.Core;
using PhoneMicBridge.Devices;
using Xunit;

namespace PhoneMicBridge.Tests
{
    /// <summary>
    /// Empirical Challenger Verification Test Suite for Milestone M3 (F-GUI-03).
    /// Adversarial stress-testing harness designed to find concurrency bugs, deadlocks,
    /// cancellation races, resource leaks, and preflight gating bypasses.
    /// </summary>
    public class EmpiricalM3ConcurrencyAndCancellationTests
    {
        #region Reflection Helpers & Test Harness Stubs

        private static SemaphoreSlim GetStateLock(BridgeApplicationService service)
        {
            var field = typeof(BridgeApplicationService).GetField("_stateLock", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("_stateLock field not found.");
            return (SemaphoreSlim)field.GetValue(service)!;
        }

        private static BridgeStateMachine GetStateMachine(BridgeApplicationService service)
        {
            var field = typeof(BridgeApplicationService).GetField("_stateMachine", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("_stateMachine field not found.");
            return (BridgeStateMachine)field.GetValue(service)!;
        }

        private static object? GetAudioEngine(BridgeApplicationService service)
        {
            var field = typeof(BridgeApplicationService).GetField("_audioEngine", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("_audioEngine field not found.");
            return field.GetValue(service);
        }

        private static void SetField<T>(object instance, string fieldName, T value)
        {
            var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new ArgumentException($"Field '{fieldName}' not found on type '{instance.GetType().FullName}'.");
            field.SetValue(instance, value);
        }

        private static void PutServiceInStreamingState(BridgeApplicationService service, string renderId = "MOCK_RENDER_EMP", string captureId = "MOCK_CAPTURE_EMP")
        {
            SetField(service, "_selectedRenderDeviceId", renderId);
            SetField(service, "_selectedRenderDeviceName", "Mock Render Device");
            SetField(service, "_selectedCaptureDeviceId", captureId);
            SetField(service, "_selectedCaptureDeviceName", "Mock Capture Device");

            var stateMachine = GetStateMachine(service);
            stateMachine.TransitionTo(BridgeState.Connecting);
            stateMachine.TransitionTo(BridgeState.Streaming);

            var publishMethod = typeof(BridgeApplicationService).GetMethod("PublishSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("PublishSnapshot not found.");
            publishMethod.Invoke(service, null);
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

        private class ControllableMockDiagnosticsEngine : IPreflightDiagnosticsEngine
        {
            private readonly Func<CancellationToken, Task<IReadOnlyList<PreflightItemDto>>> _handler;
            private int _callCount;

            public int CallCount => Volatile.Read(ref _callCount);

            public ControllableMockDiagnosticsEngine(Func<CancellationToken, Task<IReadOnlyList<PreflightItemDto>>>? handler = null)
            {
                _handler = handler ?? (_ => Task.FromResult<IReadOnlyList<PreflightItemDto>>(new List<PreflightItemDto>
                {
                    new("CHECK_MIC", "Microphone", "Pass", "Ready", "None"),
                    new("CHECK_RENDER", "Render", "Pass", "Ready", "None"),
                    new("CHECK_BT", "Bluetooth", "Pass", "Ready", "None")
                }));
            }

            public async Task<IReadOnlyList<PreflightItemDto>> ExecuteChecksAsync(
                string? captureDeviceId,
                string? renderDeviceId,
                CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _callCount);
                return await _handler(cancellationToken).ConfigureAwait(false);
            }
        }

        #endregion

        #region 1. Concurrency & Burst Stress Tests

        [Fact]
        public async Task Burst_ConcurrentStartAndStop_50Callers_NeverDeadlocksAndReleasesLock()
        {
            // Arrange
            var mockDiagnostics = new ControllableMockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);
            await service.RunPreflightDiagnosticsAsync();

            var semaphore = GetStateLock(service);
            const int callers = 50;
            var tasks = new List<Task>();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10)); // Strict 10s deadlock ceiling

            // Act: 50 concurrent callers interleaving Start and Stop
            for (int i = 0; i < callers; i++)
            {
                int index = i;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (index % 2 == 0)
                        {
                            await service.StopBridgeAsync(cts.Token);
                        }
                        else
                        {
                            await service.StartBridgeAsync(cts.Token);
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Expected in test environment without physical Bluetooth HFP endpoint
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected if token timed out or canceled
                    }
                }, cts.Token));
            }

            await Task.WhenAll(tasks);

            // Assert 1: Semaphore must be completely unheld (CurrentCount == 1)
            Assert.Equal(1, semaphore.CurrentCount);

            // Assert 2: Final state must be a valid state enum
            var finalState = service.CurrentSnapshot.State;
            Assert.Contains(finalState, new[]
            {
                BridgeState.Disconnected,
                BridgeState.Stopped,
                BridgeState.Error,
                BridgeState.Streaming
            });

            // Assert 3: Liveness check - Subsequent call must complete immediately without hanging
            using var livenessCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await service.StopBridgeAsync(livenessCts.Token);
            Assert.Equal(1, semaphore.CurrentCount);
        }

        [Fact]
        public async Task Burst_RapidSequentialPingPong_StartStop100Times_MaintainsCoherence()
        {
            // Arrange
            var mockDiagnostics = new ControllableMockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);
            await service.RunPreflightDiagnosticsAsync();

            var semaphore = GetStateLock(service);

            // Act: 100 rapid sequential cycles of Start -> Stop
            for (int i = 0; i < 100; i++)
            {
                try
                {
                    await service.StartBridgeAsync();
                }
                catch (InvalidOperationException)
                {
                    // Expected when no physical device is connected
                }

                await service.StopBridgeAsync();

                // Lock must be released after each iteration
                Assert.Equal(1, semaphore.CurrentCount);
            }

            // Assert
            Assert.Equal(1, semaphore.CurrentCount);
            Assert.Equal(BridgeState.Stopped, service.CurrentSnapshot.State);
            Assert.Null(GetAudioEngine(service));
        }

        [Fact]
        public async Task Burst_ConcurrentStart_25Callers_AllHandledCleanlyWithoutCorruptingLock()
        {
            // Arrange
            var mockDiagnostics = new ControllableMockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);
            await service.RunPreflightDiagnosticsAsync();

            var semaphore = GetStateLock(service);
            const int callers = 25;
            var tasks = new List<Task>();
            int exceptions = 0;

            // Act: 25 callers all calling StartBridgeAsync simultaneously
            for (int i = 0; i < callers; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await service.StartBridgeAsync();
                    }
                    catch (InvalidOperationException)
                    {
                        Interlocked.Increment(ref exceptions);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // Assert: All 25 callers either succeeded or threw expected hardware exception, lock is free
            Assert.Equal(callers, exceptions);
            Assert.Equal(1, semaphore.CurrentCount);
            Assert.Equal(BridgeState.Error, service.CurrentSnapshot.State);
        }

        [Fact]
        public async Task Burst_MixedOperations_StartStopDiagnosticsSelectDevices_NeverDeadlocks()
        {
            // Arrange
            var mockDiagnostics = new ControllableMockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);
            var semaphore = GetStateLock(service);
            const int callers = 30;
            var tasks = new List<Task>();
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // Act: 30 concurrent workers calling heterogeneous operations
            for (int i = 0; i < callers; i++)
            {
                int op = i % 5;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        switch (op)
                        {
                            case 0:
                                await service.StartBridgeAsync(cts.Token);
                                break;
                            case 1:
                                await service.StopBridgeAsync(cts.Token);
                                break;
                            case 2:
                                await service.RunPreflightDiagnosticsAsync(cts.Token);
                                break;
                            case 3:
                                await service.SelectDevicesAsync("CAPTURE_ID", "RENDER_ID");
                                break;
                            case 4:
                                service.ClearError();
                                _ = service.GetCurrentMeterValues();
                                break;
                        }
                    }
                    catch (InvalidOperationException)
                    {
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }, cts.Token));
            }

            await Task.WhenAll(tasks);

            // Assert: Semaphore is fully released
            Assert.Equal(1, semaphore.CurrentCount);

            // Snapshot must be internally consistent
            var snapshot = service.CurrentSnapshot;
            Assert.NotNull(snapshot);
            Assert.Equal(snapshot.State, GetStateMachine(service).CurrentState);
        }

        #endregion

        #region 2. Cancellation Stress Tests

        [Fact]
        public async Task Cancellation_PreCancelledToken_StartBridgeAsync_ThrowsImmediatelyAndLeavesLockFree()
        {
            // Arrange
            var mockDiagnostics = new ControllableMockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);
            await service.RunPreflightDiagnosticsAsync();

            var semaphore = GetStateLock(service);
            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Pre-cancel

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await service.StartBridgeAsync(cts.Token);
            });

            // State unchanged, lock released
            Assert.Equal(1, semaphore.CurrentCount);
            Assert.Equal(BridgeState.Disconnected, service.CurrentSnapshot.State);
            Assert.Null(GetAudioEngine(service));
        }

        [Fact]
        public async Task Cancellation_PreCancelledToken_StopBridgeAsync_ThrowsImmediatelyAndLeavesLockFree()
        {
            // Arrange
            var mockDiagnostics = new ControllableMockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);
            var semaphore = GetStateLock(service);

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Pre-cancel

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await service.StopBridgeAsync(cts.Token);
            });

            Assert.Equal(1, semaphore.CurrentCount);
            Assert.Equal(BridgeState.Disconnected, service.CurrentSnapshot.State);
        }

        [Fact]
        public async Task Cancellation_DuringConnectingState_StressLoop50Times_TransitionsToStoppedAndFreesLock()
        {
            // Arrange
            var mockDiagnostics = new ControllableMockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);
            await service.RunPreflightDiagnosticsAsync();

            var semaphore = GetStateLock(service);

            // Act: 50 iterations of cancelling exactly when state enters Connecting
            for (int i = 0; i < 50; i++)
            {
                using var cts = new CancellationTokenSource();

                void Handler(object? sender, BridgeUiSnapshot snapshot)
                {
                    if (snapshot.State == BridgeState.Connecting)
                    {
                        cts.Cancel();
                    }
                }

                service.SnapshotChanged += Handler;

                try
                {
                    await service.StartBridgeAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    // Expected cancellation
                }
                catch (InvalidOperationException)
                {
                    // If device lookup throws before cancellation triggers
                }
                finally
                {
                    service.SnapshotChanged -= Handler;
                }

                // Assert invariant after each cycle
                Assert.Equal(1, semaphore.CurrentCount);
                Assert.Null(GetAudioEngine(service));

                var state = service.CurrentSnapshot.State;
                Assert.True(state == BridgeState.Stopped || state == BridgeState.Error,
                    $"State after cancellation must be Stopped or Error, got: {state}");
            }
        }

        [Fact]
        public async Task Cancellation_WhileWaitingForLock_ContentionCancellation_DeregistersCleanly()
        {
            // Arrange
            var mockDiagnostics = new ControllableMockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);
            await service.RunPreflightDiagnosticsAsync();

            var semaphore = GetStateLock(service);

            // Acquire the lock explicitly to simulate a long-running operation
            await semaphore.WaitAsync();
            Assert.Equal(0, semaphore.CurrentCount);

            using var cts = new CancellationTokenSource();
            var waitingTask = Task.Run(async () =>
            {
                await service.StartBridgeAsync(cts.Token);
            });

            // Allow waitingTask to enter WaitAsync
            await Task.Delay(50);

            // Cancel waiting task while it is blocked waiting for semaphore
            cts.Cancel();

            // Act & Assert: waitingTask throws OperationCanceledException
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await waitingTask);

            // Release the holder task lock
            semaphore.Release();
            Assert.Equal(1, semaphore.CurrentCount);

            // Verify another task can now acquire and execute without issues
            using var livenessCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await service.StopBridgeAsync(livenessCts.Token);
            Assert.Equal(1, semaphore.CurrentCount);
        }

        [Fact]
        public async Task Cancellation_ConcurrentBursts_RandomDelays_NoHangsOrResourceLeaks()
        {
            // Arrange
            var mockDiagnostics = new ControllableMockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);
            await service.RunPreflightDiagnosticsAsync();

            var semaphore = GetStateLock(service);
            const int callers = 20;
            var tasks = new List<Task>();
            var random = new Random(42);

            // Act: 20 callers, each with CTS cancelled after random delay (0 - 15ms)
            for (int i = 0; i < callers; i++)
            {
                int delayMs = random.Next(0, 15);
                tasks.Add(Task.Run(async () =>
                {
                    using var cts = new CancellationTokenSource();
                    if (delayMs == 0)
                    {
                        cts.Cancel();
                    }
                    else
                    {
                        cts.CancelAfter(delayMs);
                    }

                    try
                    {
                        await service.StartBridgeAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                    catch (InvalidOperationException)
                    {
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // Assert: Semaphore is released, no audio engine leaked
            Assert.Equal(1, semaphore.CurrentCount);
            Assert.Null(GetAudioEngine(service));
        }

        #endregion

        #region 3. Preflight InProgress Gating Tests

        [Fact]
        public async Task PreflightGating_WhenInProgress_StartBridgeAsyncThrowsInvalidOperationException()
        {
            // Arrange
            var tcs = new TaskCompletionSource<IReadOnlyList<PreflightItemDto>>();
            var mockDiagnostics = new ControllableMockDiagnosticsEngine(_ => tcs.Task);
            using var service = new BridgeApplicationService(mockDiagnostics);

            // Start preflight diagnostics in background (deliberately unresolved)
            var diagTask = service.RunPreflightDiagnosticsAsync();

            // Verify snapshot reflects InProgress
            Assert.Equal(DiagnosticOverallStatus.InProgress, service.CurrentSnapshot.PreflightOverall);
            Assert.False(service.CurrentSnapshot.CanStartSession);

            // Act & Assert: StartBridgeAsync MUST throw InvalidOperationException
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await service.StartBridgeAsync();
            });

            Assert.Contains("in progress", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Resolve the diagnostics to Pass
            tcs.SetResult(new List<PreflightItemDto>
            {
                new("CHECK_MIC", "Microphone", "Pass", "OK", "None"),
                new("CHECK_RENDER", "Render", "Pass", "OK", "None"),
                new("CHECK_BT", "Bluetooth", "Pass", "OK", "None")
            });

            await diagTask;

            // Preflight is now Pass, CanStartSession is true
            Assert.Equal(DiagnosticOverallStatus.Pass, service.CurrentSnapshot.PreflightOverall);
            Assert.True(service.CurrentSnapshot.CanStartSession);
        }

        [Fact]
        public async Task PreflightGating_ConcurrentBurstCallers_AllBlockedWhileInProgress()
        {
            // Arrange
            var tcs = new TaskCompletionSource<IReadOnlyList<PreflightItemDto>>();
            var mockDiagnostics = new ControllableMockDiagnosticsEngine(_ => tcs.Task);
            using var service = new BridgeApplicationService(mockDiagnostics);

            var diagTask = service.RunPreflightDiagnosticsAsync();
            Assert.Equal(DiagnosticOverallStatus.InProgress, service.CurrentSnapshot.PreflightOverall);

            const int callers = 20;
            var tasks = new List<Task>();
            int blockedCount = 0;

            // Act: 20 concurrent callers try to call StartBridgeAsync while Preflight is InProgress
            for (int i = 0; i < callers; i++)
            {
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await service.StartBridgeAsync();
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("in progress", StringComparison.OrdinalIgnoreCase))
                    {
                        Interlocked.Increment(ref blockedCount);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // Assert: Exactly all 20 callers were blocked with InvalidOperationException
            Assert.Equal(callers, blockedCount);

            // State was never changed to Connecting or Streaming
            Assert.Equal(BridgeState.Disconnected, service.CurrentSnapshot.State);

            // Resolve diagnostics
            tcs.SetResult(new List<PreflightItemDto>
            {
                new("CHECK_MIC", "Microphone", "Pass", "OK", "None"),
                new("CHECK_RENDER", "Render", "Pass", "OK", "None"),
                new("CHECK_BT", "Bluetooth", "Pass", "OK", "None")
            });
            await diagTask;

            Assert.True(service.CurrentSnapshot.CanStartSession);
            Assert.Equal(1, GetStateLock(service).CurrentCount);
        }

        [Fact]
        public void PreflightGating_MainViewModel_PropertiesReflectDisabledDuringInProgress()
        {
            // Arrange
            var mockDiagnostics = new ControllableMockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            SetField(service, "_preflightOverall", DiagnosticOverallStatus.InProgress);
            var publishMethod = typeof(BridgeApplicationService).GetMethod("PublishSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("PublishSnapshot not found");
            publishMethod.Invoke(service, null);

            // Act
            var vm = CreateMainViewModel(service);

            // Assert
            bool canStart = GetProperty<bool>(vm, "CanStart");
            Assert.False(canStart);

            var startCmd = GetProperty<ICommand>(vm, "StartCommand");
            Assert.False(startCmd.CanExecute(null));

            string safetyText = GetProperty<string>(vm, "SafetyInterlockStatusText");
            Assert.Contains("In progress", safetyText, StringComparison.OrdinalIgnoreCase);

            string tooltip = GetProperty<string>(vm, "StartButtonTooltip");
            Assert.Contains("in progress", tooltip, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region 4. Post-Failure Liveness & Error Recovery

        [Fact]
        public async Task PostFailure_AfterHardwareMissingException_SemaphoreIsReleasedAndClearErrorRecovers()
        {
            // Arrange
            var mockDiagnostics = new ControllableMockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);
            await service.RunPreflightDiagnosticsAsync();

            var semaphore = GetStateLock(service);

            // Act 1: Call StartBridgeAsync without hardware - throws InvalidOperationException
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await service.StartBridgeAsync();
            });

            Assert.Contains("device", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Assert: Semaphore is released, State is Error, LastError is recorded
            Assert.Equal(1, semaphore.CurrentCount);
            Assert.Equal(BridgeState.Error, service.CurrentSnapshot.State);
            Assert.NotNull(service.CurrentSnapshot.LastError);

            // Act 2: User clears error
            service.ClearError();

            // Assert: State is restored to Disconnected, error cleared, CanStartSession true
            Assert.Equal(BridgeState.Disconnected, service.CurrentSnapshot.State);
            Assert.Null(service.CurrentSnapshot.LastError);
            Assert.True(service.CurrentSnapshot.CanStartSession);
            Assert.Equal(1, semaphore.CurrentCount);
        }

        [Fact]
        public async Task StopBridgeAsync_WhenAudioEngineIsStreaming_DisposesEngineCleanly()
        {
            // Arrange
            var mockDiagnostics = new ControllableMockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            PutServiceInStreamingState(service);
            Assert.Equal(BridgeState.Streaming, service.CurrentSnapshot.State);

            // Act: Stop the bridge
            await service.StopBridgeAsync();

            // Assert: State is Stopped, lock is released, audio engine is null
            Assert.Equal(BridgeState.Stopped, service.CurrentSnapshot.State);
            Assert.Equal(1, GetStateLock(service).CurrentCount);
            Assert.Null(GetAudioEngine(service));
        }

        #endregion
    }
}
