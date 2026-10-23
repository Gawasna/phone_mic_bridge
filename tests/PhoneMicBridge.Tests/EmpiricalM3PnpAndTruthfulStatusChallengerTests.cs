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
    /// Empirical Challenger verification test suite for Milestone M3.
    /// Focuses adversarially on PnP Sudden Disconnection, Error Recovery,
    /// and the Truthful Status Invariant contract.
    /// </summary>
    public class EmpiricalM3PnpAndTruthfulStatusChallengerTests
    {
        #region Test Mocks and Reflection Helpers

        private class MockDiagnosticsEngine : IPreflightDiagnosticsEngine
        {
            public IReadOnlyList<PreflightItemDto> ItemsToReturn { get; set; } = new List<PreflightItemDto>
            {
                new("CHECK_MIC_INPUT", "Microphone Input Check", "Pass", "Ready", "None"),
                new("CHECK_RENDER_DEVICE", "Target Output Device Check", "Pass", "Ready", "None"),
                new("CHECK_BLUETOOTH_TRANSPORT", "Bluetooth HFP Transport Check", "Pass", "Ready", "None")
            };

            public Task<IReadOnlyList<PreflightItemDto>> ExecuteChecksAsync(
                string? captureDeviceId,
                string? renderDeviceId,
                CancellationToken cancellationToken = default)
            {
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

        private static void ForceStreamingState(BridgeApplicationService service, string renderId = "TEST_RENDER_GUID_01", string captureId = "TEST_CAPTURE_GUID_01")
        {
            SetField(service, "_selectedRenderDeviceId", renderId);
            SetField(service, "_selectedRenderDeviceName", "Mock Bluetooth Hands-Free AG Audio");
            SetField(service, "_selectedCaptureDeviceId", captureId);
            SetField(service, "_selectedCaptureDeviceName", "Mock Realtek Microphone");

            var stateMachine = GetStateMachine(service);
            stateMachine.TransitionTo(BridgeState.Connecting);
            stateMachine.TransitionTo(BridgeState.Streaming);

            var publishMethod = typeof(BridgeApplicationService).GetMethod("PublishSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("PublishSnapshot not found.");
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

        private static void TriggerAudioEngineWarning(BridgeApplicationService service, string warning)
        {
            var method = typeof(BridgeApplicationService).GetMethod(
                "OnAudioEngineWarning",
                BindingFlags.NonPublic | BindingFlags.Instance,
                new[] { typeof(object), typeof(string) }
            ) ?? throw new InvalidOperationException("OnAudioEngineWarning method not found.");

            method.Invoke(service, new object[] { service, warning });
        }

        private static object CreateMainViewModel(IBridgeApplicationService service)
        {
            string testDir = AppContext.BaseDirectory;
            string guiDllPath = Path.GetFullPath(Path.Combine(testDir, @"..\..\..\..\..\src\PhoneMicBridge.Gui\bin\Debug\net9.0-windows10.0.19041.0\PhoneMicBridge.Gui.dll"));

            if (!File.Exists(guiDllPath))
            {
                string solutionRoot = Path.GetFullPath(Path.Combine(testDir, @"..\..\..\..\..\"));
                guiDllPath = Path.Combine(solutionRoot, "src", "PhoneMicBridge.Gui", "bin", "Debug", "net9.0-windows", "PhoneMicBridge.Gui.dll");
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

        #region 1. PnP Sudden Disconnection Empirical Tests

        [Theory]
        [InlineData(DeviceState.NotPresent)]
        [InlineData(DeviceState.Unplugged)]
        [InlineData(DeviceState.Disabled)]
        public void PnP_RenderDevice_Disconnected_TransitionsToError_WithAccurateLastError(DeviceState terminalState)
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            const string renderId = "{0.0.0.00000000}.{RENDER-ENDPOINT-PnP-001}";
            ForceStreamingState(service, renderId: renderId);

            Assert.Equal(BridgeState.Streaming, service.CurrentSnapshot.State);
            Assert.Equal(EndpointRenderStatus.Active, service.CurrentSnapshot.RenderStatus);

            // Act: Render endpoint changes from Active to terminalState
            TriggerDeviceStateChanged(service, renderId, terminalState);

            // Assert: Must transition to Error, LastError set to exact technical phrase
            var snapshot = service.CurrentSnapshot;
            Assert.Equal(BridgeState.Error, snapshot.State);
            Assert.Equal("Target HFP device disconnected", snapshot.LastError);
            Assert.Equal(EndpointRenderStatus.Error, snapshot.RenderStatus);
            Assert.Equal(BluetoothTransportStatus.Disconnected, snapshot.BluetoothStatus);
            Assert.Equal(DeliveryAckStatus.Unconfirmed, snapshot.DeliveryStatus);
        }

        [Theory]
        [InlineData(DeviceState.NotPresent)]
        [InlineData(DeviceState.Unplugged)]
        [InlineData(DeviceState.Disabled)]
        public void PnP_CaptureDevice_Disconnected_TransitionsToError_WithAccurateLastError(DeviceState terminalState)
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            const string captureId = "{0.0.1.00000000}.{MIC-CAPTURE-PnP-002}";
            ForceStreamingState(service, captureId: captureId);

            Assert.Equal(BridgeState.Streaming, service.CurrentSnapshot.State);

            // Act: Microphone endpoint changes from Active to terminalState
            TriggerDeviceStateChanged(service, captureId, terminalState);

            // Assert: Must transition to Error safely without crash
            var snapshot = service.CurrentSnapshot;
            Assert.Equal(BridgeState.Error, snapshot.State);
            Assert.Equal("Target HFP device disconnected", snapshot.LastError);
            Assert.Equal(EndpointRenderStatus.Error, snapshot.RenderStatus);
        }

        [Fact]
        public void PnP_DeviceId_CaseInsensitiveMatching_EnsuresGuidCasingTolerance()
        {
            // Arrange: Device registered with mixed/uppercase GUID
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            const string registeredRenderId = "{0.0.0.00000000}.{ABCDEF12-3456-7890-ABCD-EF1234567890}";
            const string incomingNotificationId = "{0.0.0.00000000}.{abcdef12-3456-7890-abcd-ef1234567890}";

            ForceStreamingState(service, renderId: registeredRenderId);

            // Act: Notification received with lower-cased GUID
            TriggerDeviceStateChanged(service, incomingNotificationId, DeviceState.NotPresent);

            // Assert: System must match case-insensitively and handle disconnection
            Assert.Equal(BridgeState.Error, service.CurrentSnapshot.State);
            Assert.Equal("Target HFP device disconnected", service.CurrentSnapshot.LastError);
        }

        [Fact]
        public void PnP_UnrelatedDevice_Disconnection_DoesNotAffectStreamingSession()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            const string renderId = "{0.0.0.00000000}.{TARGET-RENDER-DEVICE}";
            const string captureId = "{0.0.1.00000000}.{TARGET-MIC-DEVICE}";
            const string unrelatedDeviceId = "{0.0.0.00000000}.{UNRELATED-USB-HEADPHONES}";

            ForceStreamingState(service, renderId: renderId, captureId: captureId);

            // Act: Unrelated device disconnected
            TriggerDeviceStateChanged(service, unrelatedDeviceId, DeviceState.NotPresent);

            // Assert: Streaming state must be undisturbed
            Assert.Equal(BridgeState.Streaming, service.CurrentSnapshot.State);
            Assert.Null(service.CurrentSnapshot.LastError);
            Assert.Equal(EndpointRenderStatus.Active, service.CurrentSnapshot.RenderStatus);
            Assert.Equal(BluetoothTransportStatus.ScoActive, service.CurrentSnapshot.BluetoothStatus);
        }

        [Fact]
        public void PnP_DeviceRemoval_DeviceNotificationEventArgs_TransitionsToError()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            const string renderId = "{0.0.0.00000000}.{REMOVED-RENDER-DEVICE}";
            ForceStreamingState(service, renderId: renderId);

            // Act: DeviceRemoved event triggers OnDevicesListMutated
            TriggerDeviceListMutated(service, renderId);

            // Assert
            Assert.Equal(BridgeState.Error, service.CurrentSnapshot.State);
            Assert.Equal("Target HFP device disconnected", service.CurrentSnapshot.LastError);
        }

        [Fact]
        public void PnP_DeviceRemoval_UnrelatedDevice_DoesNotInterruptStreaming()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            const string renderId = "{0.0.0.00000000}.{ACTIVE-RENDER}";
            const string unrelatedId = "{0.0.0.00000000}.{REMOVED-SPEAKER}";
            ForceStreamingState(service, renderId: renderId);

            // Act
            TriggerDeviceListMutated(service, unrelatedId);

            // Assert: Session remains intact
            Assert.Equal(BridgeState.Streaming, service.CurrentSnapshot.State);
            Assert.Null(service.CurrentSnapshot.LastError);
        }

        [Fact]
        public void PnP_AudioEngineWarning_TargetDeviceStateChanged_SafelyTransitionsToError()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            ForceStreamingState(service);

            // Act: Engine raises device state changed warning
            TriggerAudioEngineWarning(service, "Target HFP device state changed: DeviceState.NotPresent");

            // Assert
            Assert.Equal(BridgeState.Error, service.CurrentSnapshot.State);
            Assert.Equal("Target HFP device disconnected", service.CurrentSnapshot.LastError);
        }

        [Fact]
        public void PnP_AudioEngineWarning_GenericBufferWarning_DoesNotKillSession()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            ForceStreamingState(service);

            // Act: Engine raises non-fatal buffer underrun warning
            TriggerAudioEngineWarning(service, "WasapiRenderClient buffer underrun detected");

            // Assert: Remains Streaming, warning captured in LastError
            Assert.Equal(BridgeState.Streaming, service.CurrentSnapshot.State);
            Assert.Equal("WasapiRenderClient buffer underrun detected", service.CurrentSnapshot.LastError);
        }

        [Fact]
        public async Task PnP_Stress_BurstDisconnectionsAndReconnections_DoesNotDeadlock()
        {
            // Arrange: Stress harness simulating rapid concurrent PnP events
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            const string renderId = "{0.0.0.00000000}.{BURST-RENDER-PnP}";
            ForceStreamingState(service, renderId: renderId);

            const int iterations = 30;
            var tasks = new List<Task>();

            // Act: Launch 30 concurrent tasks firing PnP events and ClearError
            for (int i = 0; i < iterations; i++)
            {
                int index = i;
                tasks.Add(Task.Run(() =>
                {
                    if (index % 3 == 0)
                    {
                        TriggerDeviceStateChanged(service, renderId, DeviceState.NotPresent);
                    }
                    else if (index % 3 == 1)
                    {
                        TriggerDeviceListMutated(service, renderId);
                    }
                    else
                    {
                        service.ClearError();
                    }
                }));
            }

            // Must finish within 5 seconds without deadlock
            var allTasks = Task.WhenAll(tasks);
            var completedTask = await Task.WhenAny(allTasks, Task.Delay(5000));
            Assert.Same(allTasks, completedTask);

            // System state must remain in a valid defined enum
            var finalState = service.CurrentSnapshot.State;
            Assert.True(
                finalState == BridgeState.Disconnected ||
                finalState == BridgeState.Error ||
                finalState == BridgeState.Streaming,
                $"Expected valid post-churn state, got: {finalState}"
            );
        }

        [Fact]
        public async Task PnP_Concurrency_DisconnectionRaceWithStopBridgeAsync_RemainsSafe()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            const string renderId = "{0.0.0.00000000}.{RACE-RENDER-PnP}";
            ForceStreamingState(service, renderId: renderId);

            // Act: Disconnect PnP on thread A while StopBridgeAsync runs on thread B
            var disconnectTask = Task.Run(() =>
            {
                TriggerDeviceStateChanged(service, renderId, DeviceState.NotPresent);
            });

            var stopTask = Task.Run(async () =>
            {
                await service.StopBridgeAsync();
            });

            await Task.WhenAll(disconnectTask, stopTask);

            // Assert: Final state must be cleanly stopped or error, never hung or corrupted
            var finalState = service.CurrentSnapshot.State;
            Assert.True(
                finalState == BridgeState.Stopped || finalState == BridgeState.Error,
                $"State must be Stopped or Error, got: {finalState}"
            );
        }

        #endregion

        #region 2. Error Recovery Empirical Tests

        [Fact]
        public async Task ErrorRecovery_ServiceClearError_RestoresDisconnectedAndClearsLastError()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);
            await service.RunPreflightDiagnosticsAsync();

            const string renderId = "{0.0.0.00000000}.{RECOVERY-RENDER-PnP}";
            ForceStreamingState(service, renderId: renderId);

            // Disconnect to induce Error
            TriggerDeviceStateChanged(service, renderId, DeviceState.NotPresent);
            Assert.Equal(BridgeState.Error, service.CurrentSnapshot.State);
            Assert.Equal("Target HFP device disconnected", service.CurrentSnapshot.LastError);

            // Act: Recover via ClearError()
            service.ClearError();

            // Assert
            var snapshot = service.CurrentSnapshot;
            Assert.Equal(BridgeState.Disconnected, snapshot.State);
            Assert.Null(snapshot.LastError);
            Assert.Equal(EndpointRenderStatus.Inactive, snapshot.RenderStatus);
            Assert.Equal(BluetoothTransportStatus.Disconnected, snapshot.BluetoothStatus);
            Assert.True(snapshot.CanStartSession, "CanStartSession should be restored when diagnostics pass.");
        }

        [Fact]
        public void ErrorRecovery_MainViewModel_ClearErrorCommand_UpdatesAllUiTelemetry()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            const string renderId = "{0.0.0.00000000}.{VM-RECOVERY-RENDER}";
            ForceStreamingState(service, renderId: renderId);
            TriggerDeviceStateChanged(service, renderId, DeviceState.NotPresent);

            var vm = CreateMainViewModel(service);

            Assert.True(GetProperty<bool>(vm, "HasError"));
            Assert.Equal("Target HFP device disconnected", GetProperty<string>(vm, "ErrorMessage"));
            Assert.Equal("ERROR", GetProperty<string>(vm, "StateText"));

            // Act: Execute ClearErrorCommand from UI
            var clearCmd = GetProperty<ICommand>(vm, "ClearErrorCommand");
            Assert.NotNull(clearCmd);
            Assert.True(clearCmd.CanExecute(null));
            clearCmd.Execute(null);

            // Assert: Telemetry properly cleared
            Assert.False(GetProperty<bool>(vm, "HasError"));
            Assert.Equal(string.Empty, GetProperty<string>(vm, "ErrorMessage"));
            Assert.Equal("DISCONNECTED", GetProperty<string>(vm, "StateText"));
        }

        [Fact]
        public void ErrorRecovery_ClearError_IdempotentUnderSequentialBursts()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            const string renderId = "{0.0.0.00000000}.{BURST-CLEAR-RENDER}";
            ForceStreamingState(service, renderId: renderId);
            TriggerDeviceStateChanged(service, renderId, DeviceState.NotPresent);
            Assert.Equal(BridgeState.Error, service.CurrentSnapshot.State);

            // Act: Call ClearError 10 times consecutively
            for (int i = 0; i < 10; i++)
            {
                service.ClearError();
            }

            // Assert: Remains Disconnected cleanly
            Assert.Equal(BridgeState.Disconnected, service.CurrentSnapshot.State);
            Assert.Null(service.CurrentSnapshot.LastError);
        }

        [Theory]
        [InlineData(BridgeState.Disconnected)]
        [InlineData(BridgeState.Stopped)]
        public void ErrorRecovery_ClearError_WhenInNonErrorStates_IsSafeNoOp(BridgeState startingState)
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            var stateMachine = GetStateMachine(service);
            if (startingState == BridgeState.Stopped)
            {
                stateMachine.TransitionTo(BridgeState.Connecting);
                stateMachine.TransitionTo(BridgeState.Stopped);
            }

            Assert.Equal(startingState, service.CurrentSnapshot.State);

            // Act
            service.ClearError();

            // Assert: State must not be mutated
            Assert.Equal(startingState, service.CurrentSnapshot.State);
            Assert.Null(service.CurrentSnapshot.LastError);
        }

        #endregion

        #region 3. Truthful Status Display Invariant Empirical Tests

        [Fact]
        public void TruthfulStatus_StreamingState_EndpointActive_ScoActive_YetDeliveryStatusStrictlyUnconfirmed()
        {
            // Arrange: The core invariant of Honest Design.
            // Even when Tier 1 (Render) is Active AND Tier 2 (Bluetooth SCO) is Active,
            // Tier 3 (Delivery ACK) MUST remain Unconfirmed.
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            ForceStreamingState(service);
            var snapshot = service.CurrentSnapshot;

            // Assert Tier 1
            Assert.Equal(EndpointRenderStatus.Active, snapshot.RenderStatus);

            // Assert Tier 2
            Assert.Equal(BluetoothTransportStatus.ScoActive, snapshot.BluetoothStatus);

            // Assert Tier 3 Invariant: STRICTLY Unconfirmed, NEVER Confirmed
            Assert.Equal(DeliveryAckStatus.Unconfirmed, snapshot.DeliveryStatus);
            Assert.NotEqual(DeliveryAckStatus.Confirmed, snapshot.DeliveryStatus);
        }

        [Theory]
        [InlineData(BridgeState.Disconnected)]
        [InlineData(BridgeState.Connecting)]
        [InlineData(BridgeState.Streaming)]
        [InlineData(BridgeState.Stopped)]
        [InlineData(BridgeState.Error)]
        public void TruthfulStatus_FullStateMatrix_Tier3NeverConfirmed(BridgeState testState)
        {
            // Arrange: Oracle iterating across all operational lifecycle states
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            switch (testState)
            {
                case BridgeState.Disconnected:
                    // Default state
                    break;
                case BridgeState.Connecting:
                    GetStateMachine(service).TransitionTo(BridgeState.Connecting);
                    break;
                case BridgeState.Streaming:
                    ForceStreamingState(service);
                    break;
                case BridgeState.Stopped:
                    GetStateMachine(service).TransitionTo(BridgeState.Connecting);
                    GetStateMachine(service).TransitionTo(BridgeState.Stopped);
                    break;
                case BridgeState.Error:
                    ForceStreamingState(service);
                    TriggerDeviceStateChanged(service, "{RENDER-ORACLE}", DeviceState.NotPresent);
                    break;
            }

            var publishMethod = typeof(BridgeApplicationService).GetMethod("PublishSnapshot", BindingFlags.NonPublic | BindingFlags.Instance)
                ?? throw new InvalidOperationException("PublishSnapshot not found.");
            publishMethod.Invoke(service, null);

            var snapshot = service.CurrentSnapshot;

            // Invariant check: In NO lifecycle state may DeliveryStatus ever be Confirmed
            Assert.NotEqual(DeliveryAckStatus.Confirmed, snapshot.DeliveryStatus);
            Assert.Equal(DeliveryAckStatus.Unconfirmed, snapshot.DeliveryStatus);
        }

        [Fact]
        public void TruthfulStatus_MainViewModel_DeliveryStatusText_StrictHonestDesignContract()
        {
            // Arrange
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            ForceStreamingState(service);
            var vm = CreateMainViewModel(service);

            // Act
            string tier1 = GetProperty<string>(vm, "EndpointStatusText");
            string tier2 = GetProperty<string>(vm, "BluetoothStatusText");
            string tier3 = GetProperty<string>(vm, "DeliveryStatusText");

            // Assert
            Assert.Equal("Endpoint Render Active", tier1);
            Assert.Equal("HFP / SCO Channel Active", tier2);

            // Invariant: UI presentation MUST show explicit UNVERIFIED message
            Assert.Equal("UNVERIFIED (No Companion ACK Protocol)", tier3);
            Assert.DoesNotContain("CONFIRMED", tier3, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void TruthfulStatus_ServiceImplementation_PublishSnapshot_HardcodedToUnconfirmed()
        {
            // Code Oracle Test: Verifies via reflection and runtime invocation
            // that PublishSnapshot does not accept or produce Confirmed state.
            var mockDiagnostics = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockDiagnostics);

            var snapshot = service.CurrentSnapshot;
            Assert.Equal(DeliveryAckStatus.Unconfirmed, snapshot.DeliveryStatus);

            // Verify with multiple property modifications
            service.SetMicGain(1.5f);
            Assert.Equal(DeliveryAckStatus.Unconfirmed, service.CurrentSnapshot.DeliveryStatus);

            service.SetMicEnabled(false);
            Assert.Equal(DeliveryAckStatus.Unconfirmed, service.CurrentSnapshot.DeliveryStatus);

            service.SetToneEnabled(true, 440.0, 0.5f);
            Assert.Equal(DeliveryAckStatus.Unconfirmed, service.CurrentSnapshot.DeliveryStatus);
        }

        #endregion
    }
}
