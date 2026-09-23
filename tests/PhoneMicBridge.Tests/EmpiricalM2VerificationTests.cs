using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using NAudio.CoreAudioApi;
using PhoneMicBridge.Application;
using PhoneMicBridge.Core;
using PhoneMicBridge.Devices;
using Xunit;

namespace PhoneMicBridge.Tests
{
    /// <summary>
    /// Empirical Challenger Verification Test Suite for Milestone M2.
    /// Focuses on rigorous stress-testing and empirical verification of:
    /// 1. Safety Interlock Gating (StartBridgeAsync blocked when Fail, StartCommand CanExecute false).
    /// 2. Burst Diagnostics concurrency, race conditions, deadlocks, and consistency.
    /// 3. PnP events and dynamic hardware state transitions (active -> disconnected -> reconnected).
    /// </summary>
    public class EmpiricalM2VerificationTests
    {
        #region Test Fixtures & Helpers

        private static AudioDeviceInfo CreateDeviceInfo(
            int index,
            string id,
            string name,
            DeviceState state = DeviceState.Active,
            bool isTargetHfp = false)
        {
            return new AudioDeviceInfo(index, id, name, state, isTargetHfp);
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

        private static void TriggerDeviceStateChanged(BridgeApplicationService service, string deviceId, DeviceState newState)
        {
            var method = typeof(BridgeApplicationService).GetMethod(
                "OnDeviceStateChanged",
                BindingFlags.NonPublic | BindingFlags.Instance,
                new[] { typeof(object), typeof(DeviceStateChangedEventArgs) }
            ) ?? throw new InvalidOperationException("OnDeviceStateChanged method not found.");

            var eventArgs = new DeviceStateChangedEventArgs(deviceId, newState);
            method.Invoke(service, new object[] { service, eventArgs });
        }

        private static void TriggerDeviceListMutated(BridgeApplicationService service, string deviceId)
        {
            var method = typeof(BridgeApplicationService).GetMethod(
                "OnDevicesListMutated",
                BindingFlags.NonPublic | BindingFlags.Instance,
                new[] { typeof(object), typeof(DeviceNotificationEventArgs) }
            ) ?? throw new InvalidOperationException("OnDevicesListMutated method not found.");

            var eventArgs = (DeviceNotificationEventArgs)Activator.CreateInstance(typeof(DeviceNotificationEventArgs), deviceId)!;
            method.Invoke(service, new object[] { service, eventArgs });
        }

        private class ControlledDiagnosticsEngine : IPreflightDiagnosticsEngine
        {
            private readonly Func<string?, string?, CancellationToken, Task<IReadOnlyList<PreflightItemDto>>> _handler;
            private int _callCount;

            public int CallCount => Volatile.Read(ref _callCount);

            public ControlledDiagnosticsEngine(Func<string?, string?, CancellationToken, Task<IReadOnlyList<PreflightItemDto>>> handler)
            {
                _handler = handler;
            }

            public Task<IReadOnlyList<PreflightItemDto>> ExecuteChecksAsync(string? captureDeviceId, string? renderDeviceId, CancellationToken cancellationToken = default)
            {
                Interlocked.Increment(ref _callCount);
                return _handler(captureDeviceId, renderDeviceId, cancellationToken);
            }
        }

        #endregion

        #region 1. Safety Interlock Gating Stress Tests

        [Fact]
        public async Task SafetyInterlock_WhenPreflightFail_StartBridgeAsync_ThrowsAndNeverInitializesAudioEngine()
        {
            // Arrange: Diagnostics engine explicitly returning Fail items
            var failingEngine = new ControlledDiagnosticsEngine((_, _, _) =>
            {
                var items = new List<PreflightItemDto>
                {
                    new("CHECK_MIC_INPUT", "Microphone Input Check", "Pass", "Ready", "None required."),
                    new("CHECK_RENDER_DEVICE", "Target Output Device Check", "Fail", "Missing Bluetooth render endpoint", "Pair phone in Bluetooth settings"),
                    new("CHECK_BLUETOOTH_TRANSPORT", "Bluetooth Transport Readiness", "Fail", "Transport unestablished", "Enable Bluetooth")
                };
                return Task.FromResult<IReadOnlyList<PreflightItemDto>>(items);
            });

            using var service = new BridgeApplicationService(failingEngine);

            // Run preflight diagnostics to transition into Fail
            var snapshot = await service.RunPreflightDiagnosticsAsync();
            Assert.Equal(DiagnosticOverallStatus.Fail, snapshot.PreflightOverall);
            Assert.False(snapshot.CanStartSession);

            // Act & Assert 1: Calling StartBridgeAsync must throw InvalidOperationException
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await service.StartBridgeAsync();
            });
            Assert.Contains("Pre-flight diagnostics failed", ex.Message, StringComparison.OrdinalIgnoreCase);

            // Act & Assert 2: Verify audio engine was NEVER instantiated or started
            var audioEngineField = typeof(BridgeApplicationService).GetField("_audioEngine", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(audioEngineField);
            var audioEngineInstance = audioEngineField.GetValue(service);
            Assert.Null(audioEngineInstance);

            // Act & Assert 3: Verify snapshot remains safely disconnected / error, never streaming
            Assert.NotEqual(BridgeState.Streaming, service.CurrentSnapshot.State);
            Assert.NotEqual(EndpointRenderStatus.Active, service.CurrentSnapshot.RenderStatus);
            Assert.False(service.CurrentSnapshot.CanStartSession);
        }

        [Fact]
        public async Task SafetyInterlock_WhenPreflightFail_UIStartCommand_CanExecuteIsFalse_AndExecutionIsBlocked()
        {
            // Arrange: Failing diagnostics engine
            var failingEngine = new ControlledDiagnosticsEngine((_, _, _) =>
            {
                var items = new List<PreflightItemDto>
                {
                    new("CHECK_MIC_INPUT", "Microphone Input Check", "Fail", "Microphone disabled", "Enable in sound settings"),
                    new("CHECK_RENDER_DEVICE", "Target Output Device Check", "Pass", "Ready", "None required."),
                    new("CHECK_BLUETOOTH_TRANSPORT", "Bluetooth Transport Readiness", "Pass", "Ready", "None required.")
                };
                return Task.FromResult<IReadOnlyList<PreflightItemDto>>(items);
            });

            using var service = new BridgeApplicationService(failingEngine);
            var vm = CreateMainViewModel(service);

            // Run diagnostics
            await service.RunPreflightDiagnosticsAsync();

            // Act: Inspect ViewModel properties
            bool canStart = GetProperty<bool>(vm, "CanStart");
            var startCommand = GetProperty<ICommand>(vm, "StartCommand");
            string tooltip = GetProperty<string>(vm, "StartButtonTooltip");
            string interlockStatus = GetProperty<string>(vm, "SafetyInterlockStatusText");

            // Assert: CanStart and CanExecute must both be false
            Assert.False(canStart);
            Assert.False(startCommand.CanExecute(null));
            Assert.Contains("Pre-flight diagnostics failed", tooltip, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("START BLOCKED", interlockStatus, StringComparison.OrdinalIgnoreCase);

            // Act: Forcibly invoke Execute on StartCommand
            startCommand.Execute(null);

            // Assert: Audio engine remained uninstantiated, state remains non-streaming
            var audioEngineField = typeof(BridgeApplicationService).GetField("_audioEngine", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(audioEngineField);
            Assert.Null(audioEngineField.GetValue(service));
            Assert.NotEqual(BridgeState.Streaming, service.CurrentSnapshot.State);
        }

        [Fact]
        public async Task SafetyInterlock_MultipleChecksFail_PreservesAllRemediationsAndEnforcesHardLock()
        {
            // Arrange: All 3 checks fail simultaneously
            var failingEngine = new ControlledDiagnosticsEngine((_, _, _) =>
            {
                var items = new List<PreflightItemDto>
                {
                    new("CHECK_MIC_INPUT", "Mic Check", "Fail", "No mic hardware", "Plug in microphone"),
                    new("CHECK_RENDER_DEVICE", "Render Check", "Fail", "No Bluetooth device", "Pair Bluetooth phone"),
                    new("CHECK_BLUETOOTH_TRANSPORT", "Transport Check", "Fail", "Bluetooth radio off", "Turn on Bluetooth")
                };
                return Task.FromResult<IReadOnlyList<PreflightItemDto>>(items);
            });

            using var service = new BridgeApplicationService(failingEngine);
            var snapshot = await service.RunPreflightDiagnosticsAsync();

            Assert.Equal(DiagnosticOverallStatus.Fail, snapshot.PreflightOverall);
            Assert.False(snapshot.CanStartSession);
            Assert.Equal(3, snapshot.PreflightItems.Count);

            foreach (var item in snapshot.PreflightItems)
            {
                Assert.Equal("Fail", item.Status);
                Assert.False(string.IsNullOrWhiteSpace(item.RemediationText));
                Assert.NotEqual("None required.", item.RemediationText);
            }

            // Gating verification
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await service.StartBridgeAsync();
            });
        }

        #endregion

        #region 2. Burst Diagnostics Concurrency & Stress Tests

        [Fact]
        public async Task BurstDiagnostics_HighConcurrency50Callers_NeverDeadlocks_AndMaintainsConsistentSnapshot()
        {
            // Arrange: Diagnostics engine with slight delay to induce multi-threaded interleaving
            var engine = new ControlledDiagnosticsEngine(async (_, _, ct) =>
            {
                await Task.Delay(Random.Shared.Next(1, 10), ct);
                return new List<PreflightItemDto>
                {
                    new("CHECK_MIC_INPUT", "Mic", "Pass", "Active", "None required."),
                    new("CHECK_RENDER_DEVICE", "Render", "Pass", "Active", "None required."),
                    new("CHECK_BLUETOOTH_TRANSPORT", "Transport", "Pass", "Ready", "None required.")
                };
            });

            using var service = new BridgeApplicationService(engine);

            const int concurrentCallers = 50;
            var tasks = new Task<BridgeUiSnapshot>[concurrentCallers];
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // Act: Launch 50 concurrent calls to RunPreflightDiagnosticsAsync
            for (int i = 0; i < concurrentCallers; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    return await service.RunPreflightDiagnosticsAsync(timeoutCts.Token);
                });
            }

            var results = await Task.WhenAll(tasks);

            // Assert: All 50 completed without deadlock
            Assert.Equal(concurrentCallers, results.Length);
            foreach (var snap in results)
            {
                Assert.NotNull(snap);
                Assert.Equal(DiagnosticOverallStatus.Pass, snap.PreflightOverall);
                Assert.True(snap.CanStartSession);
                Assert.Equal(3, snap.PreflightItems.Count);
            }

            // Final service snapshot integrity
            var finalSnapshot = service.CurrentSnapshot;
            Assert.Equal(DiagnosticOverallStatus.Pass, finalSnapshot.PreflightOverall);
            Assert.True(finalSnapshot.CanStartSession);
            Assert.Equal(3, finalSnapshot.PreflightItems.Count);
            Assert.True(engine.CallCount >= concurrentCallers);
        }

        [Fact]
        public async Task BurstDiagnostics_InterleavedWithDeviceSelection_RemainsThreadSafeAndUncorrupted()
        {
            var captureList = new List<AudioDeviceInfo>
            {
                CreateDeviceInfo(0, "MIC_A", "Microphone A", DeviceState.Active),
                CreateDeviceInfo(1, "MIC_B", "Microphone B", DeviceState.Active)
            };
            var renderList = new List<AudioDeviceInfo>
            {
                CreateDeviceInfo(0, "RENDER_A", "Headset A", DeviceState.Active, isTargetHfp: true),
                CreateDeviceInfo(1, "RENDER_B", "Headset B", DeviceState.Active, isTargetHfp: true)
            };

            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => captureList,
                renderDevicesProvider: () => renderList,
                bluetoothRadioStateProvider: () => true
            );

            using var service = new BridgeApplicationService(engine);

            const int callers = 40;
            var tasks = new List<Task>();
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // Act: Alternating burst tasks between SelectDevicesAsync and RunPreflightDiagnosticsAsync
            for (int i = 0; i < callers; i++)
            {
                int index = i;
                if (index % 2 == 0)
                {
                    string capId = (index % 4 == 0) ? "MIC_A" : "MIC_B";
                    string renId = (index % 4 == 0) ? "RENDER_A" : "RENDER_B";
                    tasks.Add(Task.Run(async () =>
                    {
                        await service.SelectDevicesAsync(capId, renId);
                    }));
                }
                else
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        await service.RunPreflightDiagnosticsAsync(timeoutCts.Token);
                    }));
                }
            }

            await Task.WhenAll(tasks);

            // Assert: Snapshot state is completely coherent
            var snapshot = service.CurrentSnapshot;
            Assert.NotNull(snapshot);
            Assert.Equal(DiagnosticOverallStatus.Pass, snapshot.PreflightOverall);
            Assert.True(snapshot.CanStartSession);
            Assert.True(snapshot.SelectedCaptureDeviceId == "MIC_A" || snapshot.SelectedCaptureDeviceId == "MIC_B");
            Assert.True(snapshot.SelectedRenderDeviceId == "RENDER_A" || snapshot.SelectedRenderDeviceId == "RENDER_B");
        }

        [Fact]
        public async Task BurstDiagnostics_RapidCancellation_HandlesCleanlyWithoutDeadlock()
        {
            var engine = new ControlledDiagnosticsEngine(async (_, _, ct) =>
            {
                await Task.Delay(50, ct);
                return new List<PreflightItemDto>
                {
                    new("CHECK_MIC_INPUT", "Mic", "Pass", "Ready", "None required."),
                    new("CHECK_RENDER_DEVICE", "Render", "Pass", "Ready", "None required."),
                    new("CHECK_BLUETOOTH_TRANSPORT", "Transport", "Pass", "Ready", "None required.")
                };
            });

            using var service = new BridgeApplicationService(engine);

            const int callers = 25;
            var tasks = new Task[callers];

            for (int i = 0; i < callers; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(5));
                    try
                    {
                        await service.RunPreflightDiagnosticsAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected on rapid cancellation
                    }
                });
            }

            await Task.WhenAll(tasks);

            // Subsequent normal call must work cleanly and restore Pass status
            var recoverySnapshot = await service.RunPreflightDiagnosticsAsync(CancellationToken.None);
            Assert.Equal(DiagnosticOverallStatus.Pass, recoverySnapshot.PreflightOverall);
            Assert.True(recoverySnapshot.CanStartSession);
        }

        #endregion

        #region 3. PnP Hardware Disconnection Simulation Tests

        [Fact]
        public async Task PnP_RenderDeviceDisconnected_AutomaticallyTransitionsPreflightToFail_AndBlocksStart()
        {
            // Arrange: Initial setup with active devices
            var renderDevice = CreateDeviceInfo(0, "HFP_ACTIVE_01", "Galaxy S24 Hands-Free AG Audio", DeviceState.Active, isTargetHfp: true);
            var captureDevice = CreateDeviceInfo(0, "MIC_01", "USB Microphone", DeviceState.Active);

            var renderList = new List<AudioDeviceInfo> { renderDevice };
            var captureList = new List<AudioDeviceInfo> { captureDevice };

            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => captureList,
                renderDevicesProvider: () => renderList,
                bluetoothRadioStateProvider: () => true
            );

            using var service = new BridgeApplicationService(engine);
            await service.SelectDevicesAsync("MIC_01", "HFP_ACTIVE_01");

            // Initial verification: Preflight is Pass
            Assert.Equal(DiagnosticOverallStatus.Pass, service.CurrentSnapshot.PreflightOverall);
            Assert.True(service.CurrentSnapshot.CanStartSession);

            // Track snapshot updates
            var failTcs = new TaskCompletionSource<BridgeUiSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.SnapshotChanged += (s, snap) =>
            {
                if (snap.PreflightOverall == DiagnosticOverallStatus.Fail)
                {
                    failTcs.TrySetResult(snap);
                }
            };

            // Act: Simulate PnP Disconnection: Device state transitions to Unplugged in the provider
            renderList[0] = CreateDeviceInfo(0, "HFP_ACTIVE_01", "Galaxy S24 Hands-Free AG Audio", DeviceState.Unplugged, isTargetHfp: true);

            // Trigger PnP event on service
            TriggerDeviceStateChanged(service, "HFP_ACTIVE_01", DeviceState.Unplugged);

            // Await background preflight diagnostics completion (with 2s timeout)
            var completedTask = await Task.WhenAny(failTcs.Task, Task.Delay(2000));
            Assert.Equal(failTcs.Task, completedTask);

            // Assert: Snapshot must transition to Fail and block Start
            var updatedSnapshot = service.CurrentSnapshot;
            Assert.Equal(DiagnosticOverallStatus.Fail, updatedSnapshot.PreflightOverall);
            Assert.False(updatedSnapshot.CanStartSession);

            var renderItem = Assert.Single(updatedSnapshot.PreflightItems, i => i.CheckId == "CHECK_RENDER_DEVICE");
            Assert.Equal("Fail", renderItem.Status);
            Assert.Contains("inactive", renderItem.Details, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Bluetooth", renderItem.RemediationText, StringComparison.OrdinalIgnoreCase);

            var btItem = Assert.Single(updatedSnapshot.PreflightItems, i => i.CheckId == "CHECK_BLUETOOTH_TRANSPORT");
            Assert.Equal("Fail", btItem.Status);

            // Calling StartBridgeAsync must be blocked
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await service.StartBridgeAsync();
            });
        }

        [Fact]
        public async Task PnP_RenderDeviceRemoved_AutomaticallyTransitionsPreflightToFail()
        {
            // Arrange: Device physically removed from the system
            var renderDevice = CreateDeviceInfo(0, "HFP_02", "Pixel Buds Hands-Free", DeviceState.Active, isTargetHfp: true);
            var captureDevice = CreateDeviceInfo(0, "MIC_02", "Built-in Mic", DeviceState.Active);

            var renderList = new List<AudioDeviceInfo> { renderDevice };
            var captureList = new List<AudioDeviceInfo> { captureDevice };

            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => captureList,
                renderDevicesProvider: () => renderList,
                bluetoothRadioStateProvider: () => true
            );

            using var service = new BridgeApplicationService(engine);
            await service.SelectDevicesAsync("MIC_02", "HFP_02");
            Assert.Equal(DiagnosticOverallStatus.Pass, service.CurrentSnapshot.PreflightOverall);

            var failTcs = new TaskCompletionSource<BridgeUiSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.SnapshotChanged += (s, snap) =>
            {
                if (snap.PreflightOverall == DiagnosticOverallStatus.Fail)
                {
                    failTcs.TrySetResult(snap);
                }
            };

            // Act: Remove device completely from provider list
            renderList.Clear();

            // Trigger PnP DeviceRemoved event
            TriggerDeviceListMutated(service, "HFP_02");

            var completedTask = await Task.WhenAny(failTcs.Task, Task.Delay(2000));
            Assert.Equal(failTcs.Task, completedTask);

            // Assert
            var snapshot = service.CurrentSnapshot;
            Assert.Equal(DiagnosticOverallStatus.Fail, snapshot.PreflightOverall);
            Assert.False(snapshot.CanStartSession);
            var renderItem = Assert.Single(snapshot.PreflightItems, i => i.CheckId == "CHECK_RENDER_DEVICE");
            Assert.Equal("Fail", renderItem.Status);
            Assert.Contains("was not found", renderItem.Details, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task PnP_DeviceReconnected_AutomaticallyRestoresPassStatusAndEnablesStart()
        {
            // Arrange: Initially unplugged device
            var renderDeviceInactive = CreateDeviceInfo(0, "HFP_RECONNECT", "Galaxy Buds Live", DeviceState.Unplugged, isTargetHfp: true);
            var renderDeviceActive = CreateDeviceInfo(0, "HFP_RECONNECT", "Galaxy Buds Live", DeviceState.Active, isTargetHfp: true);
            var captureDevice = CreateDeviceInfo(0, "MIC_03", "Mic", DeviceState.Active);

            var renderList = new List<AudioDeviceInfo> { renderDeviceInactive };
            var captureList = new List<AudioDeviceInfo> { captureDevice };

            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => captureList,
                renderDevicesProvider: () => renderList,
                bluetoothRadioStateProvider: () => true
            );

            using var service = new BridgeApplicationService(engine);
            await service.SelectDevicesAsync("MIC_03", "HFP_RECONNECT");

            // Initial state is Fail
            Assert.Equal(DiagnosticOverallStatus.Fail, service.CurrentSnapshot.PreflightOverall);
            Assert.False(service.CurrentSnapshot.CanStartSession);

            var passTcs = new TaskCompletionSource<BridgeUiSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.SnapshotChanged += (s, snap) =>
            {
                if (snap.PreflightOverall == DiagnosticOverallStatus.Pass)
                {
                    passTcs.TrySetResult(snap);
                }
            };

            // Act: Reconnect device
            renderList[0] = renderDeviceActive;
            TriggerDeviceStateChanged(service, "HFP_RECONNECT", DeviceState.Active);

            var completedTask = await Task.WhenAny(passTcs.Task, Task.Delay(2000));
            Assert.Equal(passTcs.Task, completedTask);

            // Assert: Restored to Pass and CanStart is true
            var restoredSnapshot = service.CurrentSnapshot;
            Assert.Equal(DiagnosticOverallStatus.Pass, restoredSnapshot.PreflightOverall);
            Assert.True(restoredSnapshot.CanStartSession);
            var renderItem = Assert.Single(restoredSnapshot.PreflightItems, i => i.CheckId == "CHECK_RENDER_DEVICE");
            Assert.Equal("Pass", renderItem.Status);
        }

        [Fact]
        public async Task PnP_COMExceptionDuringInspection_SwitchesPreflightToUnknownWithRemediation()
        {
            // Arrange: Device provider throws COMException upon disconnect
            var renderList = new List<AudioDeviceInfo>
            {
                CreateDeviceInfo(0, "HFP_CRASH", "Hands-Free Device", DeviceState.Active, isTargetHfp: true)
            };
            var captureList = new List<AudioDeviceInfo>
            {
                CreateDeviceInfo(0, "MIC_04", "Mic", DeviceState.Active)
            };

            bool simulateComFailure = false;
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => captureList,
                renderDevicesProvider: () =>
                {
                    if (simulateComFailure)
                    {
                        throw new COMException("HRESULT 0x88890004: Endpoint disconnected catastrophic COM error");
                    }
                    return renderList;
                },
                bluetoothRadioStateProvider: () => true
            );

            using var service = new BridgeApplicationService(engine);
            await service.SelectDevicesAsync("MIC_04", "HFP_CRASH");
            Assert.Equal(DiagnosticOverallStatus.Pass, service.CurrentSnapshot.PreflightOverall);

            var unknownTcs = new TaskCompletionSource<BridgeUiSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.SnapshotChanged += (s, snap) =>
            {
                if (snap.PreflightOverall == DiagnosticOverallStatus.Unknown)
                {
                    unknownTcs.TrySetResult(snap);
                }
            };

            // Act: Enable COM failure and trigger PnP event
            simulateComFailure = true;
            TriggerDeviceStateChanged(service, "HFP_CRASH", DeviceState.NotPresent);

            var completedTask = await Task.WhenAny(unknownTcs.Task, Task.Delay(2000));
            Assert.Equal(unknownTcs.Task, completedTask);

            // Assert: Transitions to Unknown, has remediation guidance
            var snapshot = service.CurrentSnapshot;
            Assert.Equal(DiagnosticOverallStatus.Unknown, snapshot.PreflightOverall);

            var renderItem = Assert.Single(snapshot.PreflightItems, i => i.CheckId == "CHECK_RENDER_DEVICE");
            Assert.Equal("Unknown", renderItem.Status);
            Assert.Contains("0x88890004", renderItem.Details);
            Assert.False(string.IsNullOrWhiteSpace(renderItem.RemediationText));
        }

        #endregion
    }
}
