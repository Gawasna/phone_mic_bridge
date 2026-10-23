using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using PhoneMicBridge.Application;
using PhoneMicBridge.Core;
using Xunit;

namespace PhoneMicBridge.Tests
{
    /// <summary>
    /// Empirical Challenger Verification Test Suite for Milestone M1.
    /// Stress-tests architectural boundaries, concurrency/thread-safety bursts,
    /// and PnP device disconnection graceful recovery.
    /// </summary>
    public class EmpiricalM1VerificationTests
    {
        private class StubDiagnosticsEngine : IPreflightDiagnosticsEngine
        {
            public IReadOnlyList<PreflightItemDto> ItemsToReturn { get; set; } = new List<PreflightItemDto>
            {
                new("CHECK_MIC_INPUT", "Microphone Input", "Pass", "Ready", "None"),
                new("CHECK_RENDER_DEVICE", "Target Audio Endpoint", "Pass", "Ready", "None"),
                new("CHECK_BLUETOOTH_TRANSPORT", "Bluetooth HFP Transport", "Pass", "Ready", "None")
            };

            public Task<IReadOnlyList<PreflightItemDto>> ExecuteChecksAsync(
                string? captureDeviceId,
                string? renderDeviceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(ItemsToReturn);
            }
        }

        #region 1. Architecture Boundary Stress Tests

        [Fact]
        public void ArchitectureBoundary_GuiAssembly_HasZeroDependenciesOnNAudioOrWasapi()
        {
            // Locate PhoneMicBridge.Gui.dll
            string testDir = AppContext.BaseDirectory;
            string guiDllPath = Path.GetFullPath(Path.Combine(testDir, @"..\..\..\..\..\src\PhoneMicBridge.Gui\bin\Debug\net9.0-windows10.0.19041.0\PhoneMicBridge.Gui.dll"));

            if (!File.Exists(guiDllPath))
            {
                // Fallback search relative to solution root
                string solutionRoot = Path.GetFullPath(Path.Combine(testDir, @"..\..\..\..\..\"));
                guiDllPath = Path.Combine(solutionRoot, "src", "PhoneMicBridge.Gui", "bin", "Debug", "net9.0-windows", "PhoneMicBridge.Gui.dll");
            }

            Assert.True(File.Exists(guiDllPath), $"PhoneMicBridge.Gui.dll must exist at: {guiDllPath}");

            Assembly guiAssembly = Assembly.LoadFrom(guiDllPath);
            Assert.NotNull(guiAssembly);

            // 1.1 Check Referenced Assemblies
            var referencedAssemblies = guiAssembly.GetReferencedAssemblies();
            var violatingRefs = referencedAssemblies
                .Where(r => r.Name != null && (r.Name.Contains("NAudio", StringComparison.OrdinalIgnoreCase) ||
                                              r.Name.Contains("Wasapi", StringComparison.OrdinalIgnoreCase)))
                .Select(r => r.FullName)
                .ToList();

            Assert.Empty(violatingRefs);

            // 1.2 Check Types, Properties, Fields, and Method Signatures
            var types = guiAssembly.GetTypes();
            var violations = new List<string>();

            foreach (var type in types)
            {
                if (type.Namespace != null && (type.Namespace.Contains("NAudio", StringComparison.OrdinalIgnoreCase) ||
                                               type.Namespace.Contains("Wasapi", StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"Violating Type: {type.FullName}");
                }

                // Base Type
                if (type.BaseType != null && type.BaseType.FullName != null &&
                    (type.BaseType.FullName.Contains("NAudio", StringComparison.OrdinalIgnoreCase) ||
                     type.BaseType.FullName.Contains("Wasapi", StringComparison.OrdinalIgnoreCase)))
                {
                    violations.Add($"Violating BaseType on {type.FullName}: {type.BaseType.FullName}");
                }

                // Interfaces
                foreach (var iface in type.GetInterfaces())
                {
                    if (iface.FullName != null && (iface.FullName.Contains("NAudio", StringComparison.OrdinalIgnoreCase) ||
                                                   iface.FullName.Contains("Wasapi", StringComparison.OrdinalIgnoreCase)))
                    {
                        violations.Add($"Violating Interface on {type.FullName}: {iface.FullName}");
                    }
                }

                // Properties
                foreach (var prop in type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (prop.PropertyType.FullName != null &&
                        (prop.PropertyType.FullName.Contains("NAudio", StringComparison.OrdinalIgnoreCase) ||
                         prop.PropertyType.FullName.Contains("Wasapi", StringComparison.OrdinalIgnoreCase)))
                    {
                        violations.Add($"Violating Property on {type.FullName}.{prop.Name}: {prop.PropertyType.FullName}");
                    }
                }

                // Fields
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static))
                {
                    if (field.FieldType.FullName != null &&
                        (field.FieldType.FullName.Contains("NAudio", StringComparison.OrdinalIgnoreCase) ||
                         field.FieldType.FullName.Contains("Wasapi", StringComparison.OrdinalIgnoreCase)))
                    {
                        violations.Add($"Violating Field on {type.FullName}.{field.Name}: {field.FieldType.FullName}");
                    }
                }

                // Methods
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
                {
                    if (method.ReturnType.FullName != null &&
                        (method.ReturnType.FullName.Contains("NAudio", StringComparison.OrdinalIgnoreCase) ||
                         method.ReturnType.FullName.Contains("Wasapi", StringComparison.OrdinalIgnoreCase)))
                    {
                        violations.Add($"Violating ReturnType on {type.FullName}.{method.Name}: {method.ReturnType.FullName}");
                    }

                    foreach (var parameter in method.GetParameters())
                    {
                        if (parameter.ParameterType.FullName != null &&
                            (parameter.ParameterType.FullName.Contains("NAudio", StringComparison.OrdinalIgnoreCase) ||
                             parameter.ParameterType.FullName.Contains("Wasapi", StringComparison.OrdinalIgnoreCase)))
                        {
                            violations.Add($"Violating Parameter on {type.FullName}.{method.Name}({parameter.Name}): {parameter.ParameterType.FullName}");
                        }
                    }
                }
            }

            Assert.Empty(violations);
        }

        #endregion

        #region 2. Concurrency & Thread-Safety Stress Tests

        [Fact]
        public async Task Concurrency_BurstStopOperations_ShouldBeIdempotentAndThreadSafe()
        {
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            const int concurrentCallers = 50;
            var tasks = new Task[concurrentCallers];

            // Trigger burst of 50 simultaneous StopBridgeAsync calls
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            for (int i = 0; i < concurrentCallers; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    await service.StopBridgeAsync(cts.Token);
                });
            }

            // Assert all tasks finish without any unhandled exception or deadlock
            await Task.WhenAll(tasks);

            // Verify final state is consistent and valid
            Assert.True(service.CurrentSnapshot.State == BridgeState.Disconnected ||
                        service.CurrentSnapshot.State == BridgeState.Stopped);
        }

        [Fact]
        public async Task Concurrency_BurstInterleavedStartAndStop_ShouldNeverDeadlockOrCorruptState()
        {
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            // Pre-run diagnostics to satisfy safety interlock
            await service.RunPreflightDiagnosticsAsync();

            const int callers = 30;
            var tasks = new List<Task>();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            for (int i = 0; i < callers; i++)
            {
                if (i % 2 == 0)
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await service.StartBridgeAsync(cts.Token);
                        }
                        catch (InvalidOperationException)
                        {
                            // Expected in headless test environment lacking target Bluetooth device
                        }
                    }));
                }
                else
                {
                    tasks.Add(Task.Run(async () =>
                    {
                        await service.StopBridgeAsync(cts.Token);
                    }));
                }
            }

            // Await completion with strict timeout preventing deadlock
            var allTasks = Task.WhenAll(tasks);
            var completedTask = await Task.WhenAny(allTasks, Task.Delay(TimeSpan.FromSeconds(8)));
            Assert.Equal(allTasks, completedTask);

            // Conclude with clean stop
            await service.StopBridgeAsync();

            // Snapshot must be valid, coherent, and non-corrupted
            var finalSnapshot = service.CurrentSnapshot;
            Assert.NotNull(finalSnapshot);
            Assert.True(finalSnapshot.State == BridgeState.Stopped ||
                        finalSnapshot.State == BridgeState.Disconnected ||
                        finalSnapshot.State == BridgeState.Error);
        }

        #endregion

        #region 3. PnP Disconnection Graceful Recovery Tests

        [Fact]
        public async Task PnPDisconnection_WhenDeviceDisconnectsDuringStreaming_ShouldTransitionToErrorSafelyAndImmediatelyUpdateSnapshot()
        {
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            const string mockRenderDeviceId = "{0.0.0.00000000}.{decd7d0e-5d4f-47c4-a7be-mockdevice01}";

            // 1. Inject selected render device ID
            var renderIdField = typeof(BridgeApplicationService).GetField("_selectedRenderDeviceId", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(renderIdField);
            renderIdField.SetValue(service, mockRenderDeviceId);

            // 2. Put state machine directly into Streaming state to simulate active audio session
            var stateMachineField = typeof(BridgeApplicationService).GetField("_stateMachine", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(stateMachineField);
            var stateMachine = (BridgeStateMachine)stateMachineField.GetValue(service)!;

            // Disconnected -> Connecting -> Streaming
            stateMachine.TransitionTo(BridgeState.Connecting);
            stateMachine.TransitionTo(BridgeState.Streaming);

            Assert.Equal(BridgeState.Streaming, service.CurrentSnapshot.State);
            Assert.Equal(EndpointRenderStatus.Active, service.CurrentSnapshot.RenderStatus);

            // 3. Track snapshot notifications
            BridgeUiSnapshot? receivedSnapshot = null;
            var snapshotFiredEvent = new AutoResetEvent(false);

            service.SnapshotChanged += (s, snap) =>
            {
                if (snap.State == BridgeState.Error)
                {
                    receivedSnapshot = snap;
                    snapshotFiredEvent.Set();
                }
            };

            // 4. Simulate PnP Disconnection: Render device state changes to NotPresent
            var onDeviceStateChangedMethod = typeof(BridgeApplicationService).GetMethod(
                "OnDeviceStateChanged",
                BindingFlags.NonPublic | BindingFlags.Instance,
                new[] { typeof(object), typeof(DeviceStateChangedEventArgs) }
            );
            Assert.NotNull(onDeviceStateChangedMethod);

            var eventArgs = new DeviceStateChangedEventArgs(mockRenderDeviceId, DeviceState.NotPresent);

            // Invoke handler
            onDeviceStateChangedMethod.Invoke(service, new object[] { service, eventArgs });

            // 5. Verify snapshot updated immediately to Error
            bool eventSignaled = snapshotFiredEvent.WaitOne(TimeSpan.FromSeconds(2));
            Assert.True(eventSignaled, "SnapshotChanged event must be raised upon PnP disconnection");

            Assert.NotNull(receivedSnapshot);
            Assert.Equal(BridgeState.Error, receivedSnapshot.State);
            Assert.Equal(EndpointRenderStatus.Error, receivedSnapshot.RenderStatus);
            Assert.Contains("disconnected", receivedSnapshot.LastError, StringComparison.OrdinalIgnoreCase);

            // 6. Verify graceful recovery: calling Stop and ClearError restores system to safe state
            await service.StopBridgeAsync();
            Assert.Equal(BridgeState.Stopped, service.CurrentSnapshot.State);

            service.ClearError();
            Assert.Null(service.CurrentSnapshot.LastError);
        }

        [Fact]
        public async Task Concurrency_BurstRapidCancellation_ShouldHandleCanceledCleanly()
        {
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            const int callers = 20;
            var tasks = new Task[callers];

            for (int i = 0; i < callers; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    using var cts = new CancellationTokenSource();
                    cts.Cancel(); // Immediate cancellation
                    try
                    {
                        await service.StartBridgeAsync(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        // Expected
                    }
                    catch (InvalidOperationException)
                    {
                        // Expected if device not found or diagnostics
                    }
                });
            }

            await Task.WhenAll(tasks);

            // Verify state is clean and lock is released
            await service.StopBridgeAsync();
            Assert.True(service.CurrentSnapshot.State == BridgeState.Stopped ||
                        service.CurrentSnapshot.State == BridgeState.Disconnected ||
                        service.CurrentSnapshot.State == BridgeState.Error);
        }

        [Fact]
        public async Task PreflightSafetyInterlock_WhenCheckFails_StartBridgeAsyncAlwaysRejectsUnderConcurrentBurst()
        {
            var failingStub = new StubDiagnosticsEngine
            {
                ItemsToReturn = new List<PreflightItemDto>
                {
                    new("CHECK_MIC", "Mic", "Pass", "OK", "None"),
                    new("CHECK_RENDER", "Render", "Fail", "Missing device", "Plug device")
                }
            };

            using var service = new BridgeApplicationService(failingStub);
            await service.RunPreflightDiagnosticsAsync();
            Assert.Equal(DiagnosticOverallStatus.Fail, service.CurrentSnapshot.PreflightOverall);

            const int callers = 30;
            var tasks = new Task[callers];
            int caughtSafetyExceptions = 0;

            for (int i = 0; i < callers; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    try
                    {
                        await service.StartBridgeAsync();
                    }
                    catch (InvalidOperationException ex) when (ex.Message.Contains("Pre-flight diagnostics failed"))
                    {
                        Interlocked.Increment(ref caughtSafetyExceptions);
                    }
                });
            }

            await Task.WhenAll(tasks);

            // Every single caller was safely gated
            Assert.Equal(callers, caughtSafetyExceptions);
            Assert.NotEqual(BridgeState.Streaming, service.CurrentSnapshot.State);
            Assert.False(service.CurrentSnapshot.CanStartSession);
        }

        [Fact]
        public void PnPDisconnection_WhenDeviceDisconnectsWhileNotStreaming_ShouldNotTriggerError()
        {
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            const string mockRenderDeviceId = "{0.0.0.00000000}.{decd7d0e-5d4f-47c4-a7be-mockdevice01}";

            var renderIdField = typeof(BridgeApplicationService).GetField("_selectedRenderDeviceId", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(renderIdField);
            renderIdField.SetValue(service, mockRenderDeviceId);

            // State is Disconnected
            Assert.Equal(BridgeState.Disconnected, service.CurrentSnapshot.State);

            var onDeviceStateChangedMethod = typeof(BridgeApplicationService).GetMethod(
                "OnDeviceStateChanged",
                BindingFlags.NonPublic | BindingFlags.Instance,
                new[] { typeof(object), typeof(DeviceStateChangedEventArgs) }
            );
            Assert.NotNull(onDeviceStateChangedMethod);

            var eventArgs = new DeviceStateChangedEventArgs(mockRenderDeviceId, DeviceState.NotPresent);
            onDeviceStateChangedMethod.Invoke(service, new object[] { service, eventArgs });

            // State should remain Disconnected, no spurious Error state
            Assert.Equal(BridgeState.Disconnected, service.CurrentSnapshot.State);
            Assert.Null(service.CurrentSnapshot.LastError);
        }

        #endregion
    }
}
