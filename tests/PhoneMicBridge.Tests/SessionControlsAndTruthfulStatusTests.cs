using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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
    /// Comprehensive automated test suite for Milestone M3 (F-GUI-03: Session Controls & Truthful Status Panel).
    /// Validates thread-safety, idempotency, cancellation during Connecting, pre-flight InProgress gating,
    /// sudden PnP device disconnection, ClearError recovery, and strict 3-tier status separation.
    /// </summary>
    public class SessionControlsAndTruthfulStatusTests
    {
        #region Test Stubs & Reflection Helpers

        private class MockDiagnosticsEngine : IPreflightDiagnosticsEngine
        {
            public IReadOnlyList<PreflightItemDto> ItemsToReturn { get; set; } = new List<PreflightItemDto>
            {
                new("CHECK_MIC_INPUT", "Microphone Input Check", "Pass", "Microphone ready", "None required."),
                new("CHECK_RENDER_DEVICE", "Target Output Device Check", "Pass", "Render endpoint ready", "None required."),
                new("CHECK_BLUETOOTH_TRANSPORT", "Bluetooth HFP/SCO & Transport Readiness", "Pass", "Bluetooth ready", "None required.")
            };

            public int ExecutionCount { get; private set; }

            public Task<IReadOnlyList<PreflightItemDto>> ExecuteChecksAsync(
                string? captureDeviceId,
                string? renderDeviceId,
                CancellationToken cancellationToken = default)
            {
                ExecutionCount++;
                return Task.FromResult(ItemsToReturn);
            }
        }

        private static BridgeStateMachine GetStateMachine(BridgeApplicationService service)
        {
            var field = typeof(BridgeApplicationService).GetField("_stateMachine", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("_stateMachine field not found.");
            return (BridgeStateMachine)field.GetValue(service)!;
        }

        private static void SetField<T>(object instance, string fieldName, T value)
        {
            var field = instance.GetType().GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new ArgumentException($"Field '{fieldName}' not found on type '{instance.GetType().FullName}'.");
            field.SetValue(instance, value);
        }

        private static void PutServiceInStreamingState(BridgeApplicationService service, string renderId = "MOCK_RENDER_01", string captureId = "MOCK_CAPTURE_01")
        {
            SetField(service, "_selectedRenderDeviceId", renderId);
            SetField(service, "_selectedRenderDeviceName", "Mock Bluetooth Hands-Free");
            SetField(service, "_selectedCaptureDeviceId", captureId);
            SetField(service, "_selectedCaptureDeviceName", "Mock Realtek Microphone");

            var stateMachine = GetStateMachine(service);
            stateMachine.TransitionTo(BridgeState.Connecting);
            stateMachine.TransitionTo(BridgeState.Streaming);

            // Trigger snapshot publication to synchronize CurrentSnapshot
            var publishMethod = typeof(BridgeApplicationService).GetMethod("PublishSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("PublishSnapshot method not found.");
            publishMethod.Invoke(service, null);
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

        #endregion

        #region 1. Thread-Safety & Idempotency of Start / Stop

        [Fact]
        public async Task StartBridgeAsync_WhenAlreadyStreaming_IsSafeIdempotentNoOp()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);
            await service.RunPreflightDiagnosticsAsync();

            PutServiceInStreamingState(service);
            Assert.Equal(BridgeState.Streaming, service.CurrentSnapshot.State);

            // Act: Call StartBridgeAsync while already Streaming
            await service.StartBridgeAsync();

            // Assert: Must remain Streaming without exception
            Assert.Equal(BridgeState.Streaming, service.CurrentSnapshot.State);
            Assert.Null(service.CurrentSnapshot.LastError);
        }

        [Fact]
        public async Task StopBridgeAsync_WhenAlreadyStopped_IsSafeIdempotentNoOp()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            var stateMachine = GetStateMachine(service);
            stateMachine.TransitionTo(BridgeState.Connecting);
            stateMachine.TransitionTo(BridgeState.Stopped);

            Assert.Equal(BridgeState.Stopped, service.CurrentSnapshot.State);

            // Act: Call StopBridgeAsync multiple times sequentially
            await service.StopBridgeAsync();
            await service.StopBridgeAsync();
            await service.StopBridgeAsync();

            // Assert: Must remain Stopped safely
            Assert.Equal(BridgeState.Stopped, service.CurrentSnapshot.State);
            Assert.Null(service.CurrentSnapshot.LastError);
        }

        [Fact]
        public async Task StopBridgeAsync_WhenDisconnected_IsSafeIdempotentNoOp()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            Assert.Equal(BridgeState.Disconnected, service.CurrentSnapshot.State);

            // Act
            await service.StopBridgeAsync();

            // Assert: Stays Disconnected, no error
            Assert.Equal(BridgeState.Disconnected, service.CurrentSnapshot.State);
            Assert.Null(service.CurrentSnapshot.LastError);
        }

        [Fact]
        public async Task StartAndStop_ConcurrentBursts_AreThreadSafeAndDoNotCorruptState()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);
            await service.RunPreflightDiagnosticsAsync();

            const int callers = 20;
            var tasks = new List<Task>();
            int exceptionsEncountered = 0;

            // Act: Spawn 20 concurrent tasks calling Start and Stop interleaved
            for (int i = 0; i < callers; i++)
            {
                int index = i;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        if (index % 2 == 0)
                        {
                            await service.StopBridgeAsync();
                        }
                        else
                        {
                            // In test environment without hardware, StartBridgeAsync will throw InvalidOperationException
                            // (target device not found), which is caught safely and transitions to Error or Stopped
                            await service.StartBridgeAsync();
                        }
                    }
                    catch (InvalidOperationException)
                    {
                        // Expected when no physical audio endpoints are connected
                        Interlocked.Increment(ref exceptionsEncountered);
                    }
                }));
            }

            await Task.WhenAll(tasks);

            // Assert: State must remain valid, never corrupt or deadlock
            var finalState = service.CurrentSnapshot.State;
            Assert.True(
                finalState == BridgeState.Disconnected ||
                finalState == BridgeState.Stopped ||
                finalState == BridgeState.Error ||
                finalState == BridgeState.Streaming,
                $"Final state must be a valid state enum, got: {finalState}"
            );
        }

        #endregion

        #region 2. Cancellation Support

        [Fact]
        public async Task StartBridgeAsync_WithPreCancelledToken_ThrowsOperationCanceledException_AndPreservesSafeState()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);
            await service.RunPreflightDiagnosticsAsync();

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // Pre-cancel

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await service.StartBridgeAsync(cts.Token);
            });

            // State should remain Disconnected, lock was not deadlocked
            Assert.Equal(BridgeState.Disconnected, service.CurrentSnapshot.State);
        }

        [Fact]
        public async Task StartBridgeAsync_WhenCancelledDuringConnecting_SafelyTransitionsToStoppedAndCleansUp()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);
            await service.RunPreflightDiagnosticsAsync();

            using var cts = new CancellationTokenSource();

            // Cancel the token as soon as state machine reaches Connecting
            service.SnapshotChanged += (s, snap) =>
            {
                if (snap.State == BridgeState.Connecting)
                {
                    cts.Cancel();
                }
            };

            // Act & Assert
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await service.StartBridgeAsync(cts.Token);
            });

            // System safely transitioned to Stopped upon cancellation during Connecting
            Assert.Equal(BridgeState.Stopped, service.CurrentSnapshot.State);
            Assert.Equal(EndpointRenderStatus.Inactive, service.CurrentSnapshot.RenderStatus);
        }

        #endregion

        #region 3. Safety Interlock & Preflight InProgress Gating

        [Fact]
        public async Task StartBridgeAsync_WhenPreflightInProgress_ThrowsInvalidOperationException_AndBlocksStart()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            // Simulate diagnostics in progress
            SetField(service, "_preflightOverall", DiagnosticOverallStatus.InProgress);

            var publishMethod = typeof(BridgeApplicationService).GetMethod("PublishSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("PublishSnapshot not found");
            publishMethod.Invoke(service, null);

            Assert.False(service.CurrentSnapshot.CanStartSession);

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await service.StartBridgeAsync();
            });

            Assert.Contains("in progress", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public async Task StartBridgeAsync_WhenPreflightFailed_ThrowsInvalidOperationException_AndBlocksStart()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            // Simulate diagnostics failure
            SetField(service, "_preflightOverall", DiagnosticOverallStatus.Fail);

            var publishMethod = typeof(BridgeApplicationService).GetMethod("PublishSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("PublishSnapshot not found");
            publishMethod.Invoke(service, null);

            Assert.False(service.CurrentSnapshot.CanStartSession);

            // Act & Assert
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await service.StartBridgeAsync();
            });

            Assert.Contains("Pre-flight diagnostics failed", ex.Message, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void MainViewModel_WhenPreflightInProgress_DisablesStartButtonAndSetsWarningTooltip()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
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

            string tooltip = GetProperty<string>(vm, "StartButtonTooltip");
            Assert.Contains("Diagnostics in progress", tooltip, StringComparison.OrdinalIgnoreCase);

            string safetyText = GetProperty<string>(vm, "SafetyInterlockStatusText");
            Assert.Contains("In progress", safetyText, StringComparison.OrdinalIgnoreCase);
        }

        #endregion

        #region 4. Sudden Disconnection (PnP) & Error Handling

        [Fact]
        public void SuddenDisconnection_RenderDevice_WhenStreaming_TransitionsToErrorAndSetsLastError()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            const string renderId = "{0.0.0.00000000}.{mock-render-endpoint-1234}";
            PutServiceInStreamingState(service, renderId: renderId);

            Assert.Equal(BridgeState.Streaming, service.CurrentSnapshot.State);
            Assert.Equal(EndpointRenderStatus.Active, service.CurrentSnapshot.RenderStatus);

            // Act: Render device unplugged (state changed to NotPresent)
            TriggerDeviceStateChanged(service, renderId, DeviceState.NotPresent);

            // Assert: Safely transitions to Error
            Assert.Equal(BridgeState.Error, service.CurrentSnapshot.State);
            Assert.Equal("Target HFP device disconnected", service.CurrentSnapshot.LastError);
            Assert.Equal(EndpointRenderStatus.Error, service.CurrentSnapshot.RenderStatus);
        }

        [Fact]
        public void SuddenDisconnection_CaptureDevice_WhenStreaming_TransitionsToErrorAndSetsLastError()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            const string captureId = "{0.0.1.00000000}.{mock-mic-endpoint-5678}";
            PutServiceInStreamingState(service, captureId: captureId);

            Assert.Equal(BridgeState.Streaming, service.CurrentSnapshot.State);

            // Act: Microphone device unplugged (state changed to NotPresent)
            TriggerDeviceStateChanged(service, captureId, DeviceState.NotPresent);

            // Assert: Safely transitions to Error
            Assert.Equal(BridgeState.Error, service.CurrentSnapshot.State);
            Assert.Equal("Target HFP device disconnected", service.CurrentSnapshot.LastError);
            Assert.Equal(EndpointRenderStatus.Error, service.CurrentSnapshot.RenderStatus);
        }

        [Fact]
        public void SuddenDisconnection_ViaDeviceRemovedEvent_WhenStreaming_TransitionsToError()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            const string renderId = "{0.0.0.00000000}.{mock-render-endpoint-removed}";
            PutServiceInStreamingState(service, renderId: renderId);

            // Act: DeviceRemoved event fired
            TriggerDeviceListMutated(service, renderId);

            // Assert
            Assert.Equal(BridgeState.Error, service.CurrentSnapshot.State);
            Assert.Equal("Target HFP device disconnected", service.CurrentSnapshot.LastError);
        }

        [Fact]
        public void SuddenDisconnection_WhenNotStreaming_DoesNotTriggerErrorState()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            const string renderId = "{0.0.0.00000000}.{mock-render-endpoint-idle}";
            SetField(service, "_selectedRenderDeviceId", renderId);

            Assert.Equal(BridgeState.Disconnected, service.CurrentSnapshot.State);

            // Act: Device state changed while disconnected
            TriggerDeviceStateChanged(service, renderId, DeviceState.NotPresent);

            // Assert: Stays Disconnected, no spurious Error state
            Assert.Equal(BridgeState.Disconnected, service.CurrentSnapshot.State);
            Assert.Null(service.CurrentSnapshot.LastError);
        }

        #endregion

        #region 5. ClearError Recovery

        [Fact]
        public async Task ClearError_FromErrorState_RestoresToDisconnectedAndClearsLastError()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);
            await service.RunPreflightDiagnosticsAsync();

            const string renderId = "{0.0.0.00000000}.{mock-render-endpoint-clear}";
            PutServiceInStreamingState(service, renderId: renderId);

            // Disconnect to put into Error
            TriggerDeviceStateChanged(service, renderId, DeviceState.NotPresent);
            Assert.Equal(BridgeState.Error, service.CurrentSnapshot.State);
            Assert.Equal("Target HFP device disconnected", service.CurrentSnapshot.LastError);

            // Act: User plugs device back in and clicks Clear
            service.ClearError();

            // Assert: Error is cleared, state restored to Disconnected, CanStartSession is re-enabled
            Assert.Equal(BridgeState.Disconnected, service.CurrentSnapshot.State);
            Assert.Null(service.CurrentSnapshot.LastError);
            Assert.True(service.CurrentSnapshot.CanStartSession);
            Assert.Equal(EndpointRenderStatus.Inactive, service.CurrentSnapshot.RenderStatus);
        }

        [Fact]
        public void ClearError_WhenInDisconnectedState_IsSafeNoOp()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            Assert.Equal(BridgeState.Disconnected, service.CurrentSnapshot.State);

            // Act
            service.ClearError();

            // Assert
            Assert.Equal(BridgeState.Disconnected, service.CurrentSnapshot.State);
            Assert.Null(service.CurrentSnapshot.LastError);
        }

        [Fact]
        public void MainViewModel_ClearErrorCommand_ExecutesAndClearsErrorState()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            const string renderId = "{0.0.0.00000000}.{mock-render-endpoint-vm}";
            PutServiceInStreamingState(service, renderId: renderId);
            TriggerDeviceStateChanged(service, renderId, DeviceState.NotPresent);

            var vm = CreateMainViewModel(service);

            bool hasErrorBefore = GetProperty<bool>(vm, "HasError");
            Assert.True(hasErrorBefore);

            string errorMsgBefore = GetProperty<string>(vm, "ErrorMessage");
            Assert.Equal("Target HFP device disconnected", errorMsgBefore);

            // Act: Execute ClearErrorCommand
            var clearCmd = GetProperty<ICommand>(vm, "ClearErrorCommand");
            Assert.NotNull(clearCmd);
            clearCmd.Execute(null);

            // Assert
            bool hasErrorAfter = GetProperty<bool>(vm, "HasError");
            Assert.False(hasErrorAfter);

            string errorMsgAfter = GetProperty<string>(vm, "ErrorMessage");
            Assert.Equal(string.Empty, errorMsgAfter);

            string stateText = GetProperty<string>(vm, "StateText");
            Assert.Equal("DISCONNECTED", stateText);
        }

        #endregion

        #region 6. Truthful Status Display Separation

        [Fact]
        public void TruthfulStatusDisplay_StreamingState_MaintainsStrictThreeTierSeparation()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            PutServiceInStreamingState(service);
            var snapshot = service.CurrentSnapshot;

            // Assert Snapshot Tier Separation
            // Tier 1: Endpoint Render
            Assert.Equal(EndpointRenderStatus.Active, snapshot.RenderStatus);

            // Tier 2: Bluetooth Transport
            Assert.Equal(BluetoothTransportStatus.ScoActive, snapshot.BluetoothStatus);

            // Tier 3: Audio Delivered to Phone (Must remain Unconfirmed without companion ACK)
            Assert.Equal(DeliveryAckStatus.Unconfirmed, snapshot.DeliveryStatus);
        }

        [Fact]
        public void TruthfulStatusDisplay_DisconnectedState_MaintainsStrictThreeTierSeparation()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            var snapshot = service.CurrentSnapshot;

            // Tier 1: Inactive
            Assert.Equal(EndpointRenderStatus.Inactive, snapshot.RenderStatus);

            // Tier 2: Disconnected
            Assert.Equal(BluetoothTransportStatus.Disconnected, snapshot.BluetoothStatus);

            // Tier 3: Unconfirmed
            Assert.Equal(DeliveryAckStatus.Unconfirmed, snapshot.DeliveryStatus);
        }

        [Fact]
        public void TruthfulStatusDisplay_ConnectingState_MaintainsStrictThreeTierSeparation()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            var stateMachine = GetStateMachine(service);
            stateMachine.TransitionTo(BridgeState.Connecting);

            var publishMethod = typeof(BridgeApplicationService).GetMethod("PublishSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("PublishSnapshot not found");
            publishMethod.Invoke(service, null);

            var snapshot = service.CurrentSnapshot;

            // Tier 1: Inactive while connecting
            Assert.Equal(EndpointRenderStatus.Inactive, snapshot.RenderStatus);

            // Tier 2: Connecting
            Assert.Equal(BluetoothTransportStatus.Connecting, snapshot.BluetoothStatus);

            // Tier 3: Unconfirmed
            Assert.Equal(DeliveryAckStatus.Unconfirmed, snapshot.DeliveryStatus);
        }

        [Fact]
        public void TruthfulStatusDisplay_ErrorState_MaintainsStrictThreeTierSeparation()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            const string renderId = "MOCK_ERROR_DEV";
            PutServiceInStreamingState(service, renderId: renderId);
            TriggerDeviceStateChanged(service, renderId, DeviceState.NotPresent);

            var snapshot = service.CurrentSnapshot;

            // Tier 1: Error
            Assert.Equal(EndpointRenderStatus.Error, snapshot.RenderStatus);

            // Tier 2: Disconnected
            Assert.Equal(BluetoothTransportStatus.Disconnected, snapshot.BluetoothStatus);

            // Tier 3: Unconfirmed
            Assert.Equal(DeliveryAckStatus.Unconfirmed, snapshot.DeliveryStatus);
        }

        [Fact]
        public void TruthfulStatusDisplay_Tier3NeverInferred_EvenWhenRenderAndBtAreActive()
        {
            // Arrange: Invariant test ensuring delivery status is NEVER set to Confirmed by implication
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            PutServiceInStreamingState(service);
            var snapshot = service.CurrentSnapshot;

            // Both Tier 1 and Tier 2 are active
            Assert.Equal(EndpointRenderStatus.Active, snapshot.RenderStatus);
            Assert.Equal(BluetoothTransportStatus.ScoActive, snapshot.BluetoothStatus);

            // Invariant: DeliveryStatus MUST NOT be Confirmed without actual bidirectional ACK protocol
            Assert.NotEqual(DeliveryAckStatus.Confirmed, snapshot.DeliveryStatus);
            Assert.Equal(DeliveryAckStatus.Unconfirmed, snapshot.DeliveryStatus);
        }

        [Fact]
        public void MainViewModel_TruthfulStatusProperties_MatchExpectedEngineeringText()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            PutServiceInStreamingState(service);
            var vm = CreateMainViewModel(service);

            // Assert UI presentation text
            string endpointText = GetProperty<string>(vm, "EndpointStatusText");
            Assert.Equal("Endpoint Render Active", endpointText);

            string btText = GetProperty<string>(vm, "BluetoothStatusText");
            Assert.Equal("HFP / SCO Channel Active", btText);

            string deliveryText = GetProperty<string>(vm, "DeliveryStatusText");
            Assert.Equal("UNVERIFIED (No Companion ACK Protocol)", deliveryText);
        }

        #endregion

        #region 7. Preflight Diagnostics Bluetooth Exception Handling

        [Fact]
        public async Task PreflightDiagnosticsEngine_CheckBluetoothTransport_WhenProviderThrows_ReturnsUnknownSafely()
        {
            // Arrange: bluetoothRadioStateProvider throws unexpected exception (e.g. WinRT RPC error)
            var captureList = new List<AudioDeviceInfo>
            {
                new(0, "MIC_01", "Realtek Audio", DeviceState.Active, false)
            };
            var renderList = new List<AudioDeviceInfo>
            {
                new(0, "BT_01", "Bluetooth Hands-Free AG Audio", DeviceState.Active, true)
            };

            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => captureList,
                renderDevicesProvider: () => renderList,
                bluetoothRadioStateProvider: () => throw new InvalidOperationException("Bluetooth radio controller COM/RPC failure")
            );

            // Act
            var items = await engine.ExecuteChecksAsync("MIC_01", "BT_01");

            // Assert: Does not throw exception; Bluetooth check safely returns Unknown
            var btItem = items.FirstOrDefault(i => i.CheckId == "CHECK_BLUETOOTH_TRANSPORT");
            Assert.NotNull(btItem);
            Assert.Equal("Unknown", btItem.Status);
            Assert.Contains("Bluetooth transport check encountered error", btItem.Details);
            Assert.Contains("Device Manager", btItem.RemediationText);
        }

        #endregion
    }
}
