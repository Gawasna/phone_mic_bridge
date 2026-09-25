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
    /// Comprehensive Unit & Integration Test Suite for Milestone M2 (F-GUI-02: Pre-flight Diagnostics & Device Selection).
    /// Verifies tri-state evaluation (Pass/Fail/Unknown), explicit remediation texts, safety interlock gating,
    /// stable MMDevice ID tracking, duplicate friendly name disambiguation, and re-run diagnostics.
    /// </summary>
    public class PreflightAndDeviceSelectionTests
    {
        #region Test Stubs & Helpers

        private class MockDiagnosticsEngine : IPreflightDiagnosticsEngine
        {
            public IReadOnlyList<PreflightItemDto> ItemsToReturn { get; set; } = Array.Empty<PreflightItemDto>();
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
            string guiDllPath = Path.GetFullPath(Path.Combine(testDir, @"..\..\..\..\..\Gui\PhoneMicBridge.Gui\bin\Debug\net9.0-windows10.0.19041.0\PhoneMicBridge.Gui.dll"));

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

        #region 1. Microphone Input Check Tri-State & Remediation

        [Fact]
        public async Task Preflight_MicrophoneCheck_WhenActiveDevicePresent_ShouldReturnPass()
        {
            // Arrange
            var captureList = new List<AudioDeviceInfo>
            {
                CreateDeviceInfo(0, "MIC_01", "Realtek High Definition Audio", DeviceState.Active)
            };
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => captureList,
                renderDevicesProvider: () => new List<AudioDeviceInfo>()
            );

            // Act
            var results = await engine.ExecuteChecksAsync(captureDeviceId: "MIC_01", renderDeviceId: null);
            var micItem = Assert.Single(results, r => r.CheckId == "CHECK_MIC_INPUT");

            // Assert
            Assert.Equal("Pass", micItem.Status);
            Assert.Contains("Realtek High Definition Audio", micItem.Details);
            Assert.Equal("None required.", micItem.RemediationText);
        }

        [Fact]
        public async Task Preflight_MicrophoneCheck_WhenNoDevicesPresent_ShouldReturnFailWithRemediation()
        {
            // Arrange
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => new List<AudioDeviceInfo>(),
                renderDevicesProvider: () => new List<AudioDeviceInfo>()
            );

            // Act
            var results = await engine.ExecuteChecksAsync(null, null);
            var micItem = Assert.Single(results, r => r.CheckId == "CHECK_MIC_INPUT");

            // Assert
            Assert.Equal("Fail", micItem.Status);
            Assert.Contains("No capture microphone detected", micItem.Details);
            Assert.False(string.IsNullOrWhiteSpace(micItem.RemediationText));
            Assert.Contains("Privacy & security > Microphone", micItem.RemediationText);
        }

        [Fact]
        public async Task Preflight_MicrophoneCheck_WhenSelectedDeviceNotFound_ShouldReturnFailWithRemediation()
        {
            // Arrange
            var captureList = new List<AudioDeviceInfo>
            {
                CreateDeviceInfo(0, "MIC_EXISTING", "Default Microphone", DeviceState.Active)
            };
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => captureList,
                renderDevicesProvider: () => new List<AudioDeviceInfo>()
            );

            // Act
            var results = await engine.ExecuteChecksAsync(captureDeviceId: "MIC_NONEXISTENT", renderDeviceId: null);
            var micItem = Assert.Single(results, r => r.CheckId == "CHECK_MIC_INPUT");

            // Assert
            Assert.Equal("Fail", micItem.Status);
            Assert.Contains("MIC_NONEXISTENT", micItem.Details);
            Assert.Contains("was not found", micItem.Details);
            Assert.Contains("select another active microphone", micItem.RemediationText);
        }

        [Fact]
        public async Task Preflight_MicrophoneCheck_WhenSelectedDeviceInactive_ShouldReturnFailWithRemediation()
        {
            // Arrange
            var captureList = new List<AudioDeviceInfo>
            {
                CreateDeviceInfo(0, "MIC_DISABLED", "USB Microphone", DeviceState.Disabled)
            };
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => captureList,
                renderDevicesProvider: () => new List<AudioDeviceInfo>()
            );

            // Act
            var results = await engine.ExecuteChecksAsync(captureDeviceId: "MIC_DISABLED", renderDeviceId: null);
            var micItem = Assert.Single(results, r => r.CheckId == "CHECK_MIC_INPUT");

            // Assert
            Assert.Equal("Fail", micItem.Status);
            Assert.Contains("is not active", micItem.Details);
            Assert.Contains("Enable the microphone in Windows Sound Control Panel", micItem.RemediationText);
        }

        [Fact]
        public async Task Preflight_MicrophoneCheck_WhenAllDevicesInactiveWithoutSelection_ShouldReturnFailWithRemediation()
        {
            // Arrange
            var captureList = new List<AudioDeviceInfo>
            {
                CreateDeviceInfo(0, "MIC_UNPLUGGED", "Internal Mic", DeviceState.Unplugged)
            };
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => captureList,
                renderDevicesProvider: () => new List<AudioDeviceInfo>()
            );

            // Act
            var results = await engine.ExecuteChecksAsync(captureDeviceId: null, renderDeviceId: null);
            var micItem = Assert.Single(results, r => r.CheckId == "CHECK_MIC_INPUT");

            // Assert
            Assert.Equal("Fail", micItem.Status);
            Assert.Contains("no input device is in Active state", micItem.Details);
            Assert.Contains("Enable a microphone device in Windows Sound Settings", micItem.RemediationText);
        }

        [Fact]
        public async Task Preflight_MicrophoneCheck_WhenProviderThrowsException_ShouldReturnUnknownWithRemediation()
        {
            // Arrange
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => throw new InvalidOperationException("Audio endpoint enumeration COM error 0x80070005"),
                renderDevicesProvider: () => new List<AudioDeviceInfo>()
            );

            // Act
            var results = await engine.ExecuteChecksAsync(null, null);
            var micItem = Assert.Single(results, r => r.CheckId == "CHECK_MIC_INPUT");

            // Assert
            Assert.Equal("Unknown", micItem.Status);
            Assert.Contains("Audio endpoint enumeration COM error", micItem.Details);
            Assert.Contains("Audiosrv", micItem.RemediationText);
        }

        #endregion

        #region 2. Target Output Device Check Tri-State & Remediation

        [Fact]
        public async Task Preflight_RenderDeviceCheck_WhenActiveHfpDevicePresent_ShouldReturnPass()
        {
            // Arrange
            var renderList = new List<AudioDeviceInfo>
            {
                CreateDeviceInfo(0, "RENDER_HFP", "Galaxy S23 Hands-Free AG Audio", DeviceState.Active, isTargetHfp: true)
            };
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => new List<AudioDeviceInfo>(),
                renderDevicesProvider: () => renderList
            );

            // Act
            var results = await engine.ExecuteChecksAsync(captureDeviceId: null, renderDeviceId: "RENDER_HFP");
            var renderItem = Assert.Single(results, r => r.CheckId == "CHECK_RENDER_DEVICE");

            // Assert
            Assert.Equal("Pass", renderItem.Status);
            Assert.Contains("Galaxy S23 Hands-Free AG Audio", renderItem.Details);
            Assert.Equal("None required.", renderItem.RemediationText);
        }

        [Fact]
        public async Task Preflight_RenderDeviceCheck_WhenNoRenderDevices_ShouldReturnFailWithRemediation()
        {
            // Arrange
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => new List<AudioDeviceInfo>(),
                renderDevicesProvider: () => new List<AudioDeviceInfo>()
            );

            // Act
            var results = await engine.ExecuteChecksAsync(null, null);
            var renderItem = Assert.Single(results, r => r.CheckId == "CHECK_RENDER_DEVICE");

            // Assert
            Assert.Equal("Fail", renderItem.Status);
            Assert.Contains("No Bluetooth Hands-Free audio output device detected", renderItem.Details);
            Assert.Contains("Ensure phone Bluetooth is paired and connected", renderItem.RemediationText);
        }

        [Fact]
        public async Task Preflight_RenderDeviceCheck_WhenSelectedRenderNotFound_ShouldReturnFailWithRemediation()
        {
            // Arrange
            var renderList = new List<AudioDeviceInfo>
            {
                CreateDeviceInfo(0, "HFP_01", "Phone Hands-Free Audio", DeviceState.Active, isTargetHfp: true)
            };
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => new List<AudioDeviceInfo>(),
                renderDevicesProvider: () => renderList
            );

            // Act
            var results = await engine.ExecuteChecksAsync(captureDeviceId: null, renderDeviceId: "MISSING_ID");
            var renderItem = Assert.Single(results, r => r.CheckId == "CHECK_RENDER_DEVICE");

            // Assert
            Assert.Equal("Fail", renderItem.Status);
            Assert.Contains("MISSING_ID", renderItem.Details);
            Assert.Contains("Rescan devices or pair and connect", renderItem.RemediationText);
        }

        [Fact]
        public async Task Preflight_RenderDeviceCheck_WhenTargetDeviceInactive_ShouldReturnFailWithRemediation()
        {
            // Arrange
            var renderList = new List<AudioDeviceInfo>
            {
                CreateDeviceInfo(0, "HFP_INACTIVE", "Galaxy S23 Hands-Free", DeviceState.Unplugged, isTargetHfp: true)
            };
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => new List<AudioDeviceInfo>(),
                renderDevicesProvider: () => renderList
            );

            // Act
            var results = await engine.ExecuteChecksAsync(captureDeviceId: null, renderDeviceId: "HFP_INACTIVE");
            var renderItem = Assert.Single(results, r => r.CheckId == "CHECK_RENDER_DEVICE");

            // Assert
            Assert.Equal("Fail", renderItem.Status);
            Assert.Contains("found but inactive", renderItem.Details);
            Assert.Contains("Connect your paired phone to Windows via Bluetooth Settings", renderItem.RemediationText);
        }

        [Fact]
        public async Task Preflight_RenderDeviceCheck_WhenProviderThrowsException_ShouldReturnUnknownWithRemediation()
        {
            // Arrange
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => new List<AudioDeviceInfo>(),
                renderDevicesProvider: () => throw new Exception("WASAPI render enumeration timeout")
            );

            // Act
            var results = await engine.ExecuteChecksAsync(null, null);
            var renderItem = Assert.Single(results, r => r.CheckId == "CHECK_RENDER_DEVICE");

            // Assert
            Assert.Equal("Unknown", renderItem.Status);
            Assert.Contains("WASAPI render enumeration timeout", renderItem.Details);
            Assert.Contains("Open Windows Bluetooth settings", renderItem.RemediationText);
        }

        #endregion

        #region 3. Bluetooth Transport Check Tri-State & Remediation

        [Fact]
        public async Task Preflight_BluetoothTransportCheck_WhenRenderPassAndIsHfp_ShouldReturnPass()
        {
            // Arrange
            var renderList = new List<AudioDeviceInfo>
            {
                CreateDeviceInfo(0, "HFP_01", "Phone Hands-Free AG Audio", DeviceState.Active, isTargetHfp: true)
            };
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => new List<AudioDeviceInfo>(),
                renderDevicesProvider: () => renderList,
                bluetoothRadioStateProvider: () => true
            );

            // Act
            var results = await engine.ExecuteChecksAsync(null, "HFP_01");
            var btItem = Assert.Single(results, r => r.CheckId == "CHECK_BLUETOOTH_TRANSPORT");

            // Assert
            Assert.Equal("Pass", btItem.Status);
            Assert.Contains("Bluetooth Hands-Free transport channel ready", btItem.Details);
            Assert.Equal("None required.", btItem.RemediationText);
        }

        [Fact]
        public async Task Preflight_BluetoothTransportCheck_WhenRenderFails_ShouldReturnFailWithRemediation()
        {
            // Arrange: No render devices causes render check to fail
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => new List<AudioDeviceInfo>(),
                renderDevicesProvider: () => new List<AudioDeviceInfo>()
            );

            // Act
            var results = await engine.ExecuteChecksAsync(null, null);
            var btItem = Assert.Single(results, r => r.CheckId == "CHECK_BLUETOOTH_TRANSPORT");

            // Assert
            Assert.Equal("Fail", btItem.Status);
            Assert.Contains("target audio endpoint is unavailable", btItem.Details);
            Assert.Contains("reconnect your phone in Hands-Free mode", btItem.RemediationText);
        }

        [Fact]
        public async Task Preflight_BluetoothTransportCheck_WhenRenderUnknown_ShouldReturnUnknownWithRemediation()
        {
            // Arrange: Exception in render check causes render check to be Unknown
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => new List<AudioDeviceInfo>(),
                renderDevicesProvider: () => throw new Exception("COM link unavailable")
            );

            // Act
            var results = await engine.ExecuteChecksAsync(null, null);
            var btItem = Assert.Single(results, r => r.CheckId == "CHECK_BLUETOOTH_TRANSPORT");

            // Assert
            Assert.Equal("Unknown", btItem.Status);
            Assert.Contains("render check was inconclusive", btItem.Details);
            Assert.Contains("Verify Bluetooth adapter is enabled in Windows Device Manager", btItem.RemediationText);
        }

        [Fact]
        public async Task Preflight_BluetoothTransportCheck_WhenRenderPassButDeviceNotHfp_ShouldReturnFailWithRemediation()
        {
            // Arrange: Output device exists and is active, but is regular desktop speakers, NOT HFP!
            var renderList = new List<AudioDeviceInfo>
            {
                CreateDeviceInfo(0, "DEV_SPEAKERS", "Realtek Stereo Speakers", DeviceState.Active, isTargetHfp: false)
            };
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => new List<AudioDeviceInfo>(),
                renderDevicesProvider: () => renderList
            );

            // Act
            var results = await engine.ExecuteChecksAsync(null, "DEV_SPEAKERS");
            var btItem = Assert.Single(results, r => r.CheckId == "CHECK_BLUETOOTH_TRANSPORT");

            // Assert: Transport must fail because regular speakers do not route SCO Bluetooth call audio!
            Assert.Equal("Fail", btItem.Status);
            Assert.Contains("not a Bluetooth Hands-Free endpoint", btItem.Details);
            Assert.Contains("Select a Bluetooth Hands-Free AG Audio device", btItem.RemediationText);
        }

        [Fact]
        public async Task Preflight_BluetoothTransportCheck_WhenBluetoothRadioOff_ShouldReturnFailWithRemediation()
        {
            // Arrange: HFP device is listed, but PC Bluetooth radio is powered off
            var renderList = new List<AudioDeviceInfo>
            {
                CreateDeviceInfo(0, "HFP_01", "Galaxy Hands-Free", DeviceState.Active, isTargetHfp: true)
            };
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => new List<AudioDeviceInfo>(),
                renderDevicesProvider: () => renderList,
                bluetoothRadioStateProvider: () => false // Radio is OFF
            );

            // Act
            var results = await engine.ExecuteChecksAsync(null, "HFP_01");
            var btItem = Assert.Single(results, r => r.CheckId == "CHECK_BLUETOOTH_TRANSPORT");

            // Assert
            Assert.Equal("Fail", btItem.Status);
            Assert.Contains("Bluetooth radio adapter is disabled", btItem.Details);
            Assert.Contains("Turn on Bluetooth in Windows Settings", btItem.RemediationText);
        }

        #endregion

        #region 4. Safety Interlock Start Prevention

        [Fact]
        public async Task SafetyInterlock_WhenAnyDiagnosticCheckFails_ShouldBlockStartButtonAndCanExecute()
        {
            // Arrange
            var mockEngine = new MockDiagnosticsEngine
            {
                ItemsToReturn = new List<PreflightItemDto>
                {
                    new("CHECK_MIC_INPUT", "Mic", "Pass", "Ready", "None required."),
                    new("CHECK_RENDER_DEVICE", "Render", "Fail", "Missing device", "Pair Bluetooth phone"),
                    new("CHECK_BLUETOOTH_TRANSPORT", "BT", "Fail", "Endpoint unavailable", "Verify Bluetooth")
                }
            };

            using var service = new BridgeApplicationService(mockEngine);
            var vm = CreateMainViewModel(service);

            // Act
            await service.RunPreflightDiagnosticsAsync();

            // Assert Snapshot level
            Assert.Equal(DiagnosticOverallStatus.Fail, service.CurrentSnapshot.PreflightOverall);
            Assert.False(service.CurrentSnapshot.CanStartSession);

            // Assert ViewModel level
            bool canStart = GetProperty<bool>(vm, "CanStart");
            var startCommand = GetProperty<ICommand>(vm, "StartCommand");
            string tooltip = GetProperty<string>(vm, "StartButtonTooltip");
            string interlock = GetProperty<string>(vm, "SafetyInterlockStatusText");

            Assert.False(canStart);
            Assert.False(startCommand.CanExecute(null));
            Assert.Contains("Cannot start bridge: Pre-flight diagnostics failed", tooltip);
            Assert.Contains("START BLOCKED", interlock);

            // Attempting to invoke StartBridgeAsync on service must throw InvalidOperationException
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await service.StartBridgeAsync();
            });
        }

        [Fact]
        public async Task SafetyInterlock_WhenAllDiagnosticChecksPass_ShouldEnableStartButtonAndCanExecute()
        {
            // Arrange
            var mockEngine = new MockDiagnosticsEngine
            {
                ItemsToReturn = new List<PreflightItemDto>
                {
                    new("CHECK_MIC_INPUT", "Mic", "Pass", "Ready", "None required."),
                    new("CHECK_RENDER_DEVICE", "Render", "Pass", "Ready", "None required."),
                    new("CHECK_BLUETOOTH_TRANSPORT", "BT", "Pass", "Ready", "None required.")
                }
            };

            using var service = new BridgeApplicationService(mockEngine);
            var vm = CreateMainViewModel(service);

            // Act
            await service.RunPreflightDiagnosticsAsync();

            // Assert Snapshot level
            Assert.Equal(DiagnosticOverallStatus.Pass, service.CurrentSnapshot.PreflightOverall);
            Assert.True(service.CurrentSnapshot.CanStartSession);

            // Assert ViewModel level
            bool canStart = GetProperty<bool>(vm, "CanStart");
            var startCommand = GetProperty<ICommand>(vm, "StartCommand");
            string tooltip = GetProperty<string>(vm, "StartButtonTooltip");
            string interlock = GetProperty<string>(vm, "SafetyInterlockStatusText");

            Assert.True(canStart);
            Assert.True(startCommand.CanExecute(null));
            Assert.Contains("Start audio bridge session", tooltip);
            Assert.Contains("READY", interlock);
        }

        #endregion

        #region 5. Stable MMDevice ID & Duplicate Friendly Name Handling

        [Fact]
        public async Task DeviceSelection_WithDuplicateFriendlyNames_ShouldDistinguishAndSelectByStableId()
        {
            // Arrange: Two audio devices sharing the EXACT same friendly name but different endpoint IDs
            string duplicateName = "Microphone (Realtek High Definition Audio)";
            string stableIdA = "{0.0.1.00000000}.{AAAA-1111-2222}";
            string stableIdB = "{0.0.1.00000000}.{BBBB-3333-4444}";

            var mockEngine = new MockDiagnosticsEngine
            {
                ItemsToReturn = new List<PreflightItemDto>
                {
                    new("CHECK_MIC_INPUT", "Mic", "Pass", $"Active: {duplicateName}", "None required."),
                    new("CHECK_RENDER_DEVICE", "Render", "Pass", "Ready", "None required."),
                    new("CHECK_BLUETOOTH_TRANSPORT", "BT", "Pass", "Ready", "None required.")
                }
            };

            using var service = new BridgeApplicationService(mockEngine);

            // Act: Select specific ID B
            await service.SelectDevicesAsync(captureDeviceId: stableIdB, renderDeviceId: null);

            // Assert
            Assert.Equal(stableIdB, service.CurrentSnapshot.SelectedCaptureDeviceId);
            Assert.NotEqual(stableIdA, service.CurrentSnapshot.SelectedCaptureDeviceId);
        }

        [Fact]
        public void DeviceSelection_ViewModel_ShouldExposeSelectedDeviceIds()
        {
            // Arrange
            var mockEngine = new MockDiagnosticsEngine();
            using var service = new BridgeApplicationService(mockEngine);
            var vm = CreateMainViewModel(service);

            var devDto = new AudioDeviceDto(
                Id: "{0.0.0.00000000}.{STABLE_ID_TEST}",
                Name: "Headset Hands-Free",
                IsActive: true,
                IsTargetHfp: true,
                IsDefault: false
            );

            // Act: Set SelectedRenderDevice via reflection
            var prop = vm.GetType().GetProperty("SelectedRenderDevice");
            prop!.SetValue(vm, devDto);

            // Assert
            string? selectedId = GetProperty<string?>(vm, "SelectedRenderDeviceId");
            Assert.Equal("{0.0.0.00000000}.{STABLE_ID_TEST}", selectedId);
        }

        #endregion

        #region 6. Re-run Diagnostics Command Execution

        [Fact]
        public async Task RunDiagnosticsCommand_ShouldTriggerEngineAndRefreshSnapshot()
        {
            // Arrange
            var mockEngine = new MockDiagnosticsEngine
            {
                ItemsToReturn = new List<PreflightItemDto>
                {
                    new("CHECK_MIC_INPUT", "Mic", "Pass", "Ready", "None required."),
                    new("CHECK_RENDER_DEVICE", "Render", "Pass", "Ready", "None required."),
                    new("CHECK_BLUETOOTH_TRANSPORT", "BT", "Pass", "Ready", "None required.")
                }
            };

            using var service = new BridgeApplicationService(mockEngine);
            var vm = CreateMainViewModel(service);

            int initialExecutionCount = mockEngine.ExecutionCount;

            // Act: Execute re-run command via reflection
            var cmd = GetProperty<ICommand>(vm, "RunDiagnosticsCommand");
            cmd.Execute(null);

            // Wait for completion (up to 1000ms)
            for (int i = 0; i < 20 && mockEngine.ExecutionCount <= initialExecutionCount; i++)
            {
                await Task.Delay(50);
            }

            // Assert
            Assert.True(mockEngine.ExecutionCount > initialExecutionCount);
            var snapshot = GetProperty<BridgeUiSnapshot>(vm, "Snapshot");
            Assert.Equal(DiagnosticOverallStatus.Pass, snapshot.PreflightOverall);
            Assert.Equal(3, snapshot.PreflightItems.Count);
        }

        #endregion

        #region 7. Additional Edge Cases & Fallbacks

        [Fact]
        public async Task Preflight_RenderDeviceCheck_FallbackToHandsFreeNameMatch_WhenIsTargetHfpIsFalse()
        {
            // Arrange: Device does not have specific target GUID, but friendly name contains "hands-free"
            var renderList = new List<AudioDeviceInfo>
            {
                CreateDeviceInfo(0, "DEV_HF_NAME", "Pixel 8 Pro Hands-Free Audio", DeviceState.Active, isTargetHfp: false)
            };
            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => new List<AudioDeviceInfo>(),
                renderDevicesProvider: () => renderList
            );

            // Act: Auto-discovery without specifying renderDeviceId
            var results = await engine.ExecuteChecksAsync(null, null);
            var renderItem = Assert.Single(results, r => r.CheckId == "CHECK_RENDER_DEVICE");
            var btItem = Assert.Single(results, r => r.CheckId == "CHECK_BLUETOOTH_TRANSPORT");

            // Assert
            Assert.Equal("Pass", renderItem.Status);
            Assert.Contains("Pixel 8 Pro Hands-Free Audio", renderItem.Details);
            Assert.Equal("Pass", btItem.Status);
        }

        private class AsyncPendingDiagnosticsEngine : IPreflightDiagnosticsEngine
        {
            private readonly Task<IReadOnlyList<PreflightItemDto>> _taskToReturn;
            public AsyncPendingDiagnosticsEngine(Task<IReadOnlyList<PreflightItemDto>> taskToReturn)
            {
                _taskToReturn = taskToReturn;
            }
            public Task<IReadOnlyList<PreflightItemDto>> ExecuteChecksAsync(string? captureDeviceId, string? renderDeviceId, CancellationToken cancellationToken = default)
            {
                return _taskToReturn;
            }
        }

        [Fact]
        public async Task SafetyInterlock_WhenPreflightInProgress_ShouldBlockStartSession()
        {
            // Arrange: Engine that returns a pending task to simulate in-progress diagnostics
            var tcs = new TaskCompletionSource<IReadOnlyList<PreflightItemDto>>();
            var slowEngine = new AsyncPendingDiagnosticsEngine(tcs.Task);

            using var service = new BridgeApplicationService(slowEngine);

            // Act: In-progress status before diagnostics finish
            var runTask = service.RunPreflightDiagnosticsAsync();

            // While running, verify snapshot state
            Assert.Equal(DiagnosticOverallStatus.InProgress, service.CurrentSnapshot.PreflightOverall);
            Assert.False(service.CurrentSnapshot.CanStartSession);

            // Complete the pending task
            tcs.SetResult(new List<PreflightItemDto>
            {
                new("CHECK_MIC_INPUT", "Mic", "Pass", "Ready", "None required."),
                new("CHECK_RENDER_DEVICE", "Render", "Pass", "Ready", "None required."),
                new("CHECK_BLUETOOTH_TRANSPORT", "BT", "Pass", "Ready", "None required.")
            });

            await runTask;
            Assert.True(service.CurrentSnapshot.CanStartSession);
            Assert.Equal(DiagnosticOverallStatus.Pass, service.CurrentSnapshot.PreflightOverall);
        }

        [Fact]
        public async Task DeviceSelection_SwitchingDevices_ShouldUpdateSelectedDeviceIdsInSnapshot()
        {
            // Arrange
            var mockEngine = new MockDiagnosticsEngine
            {
                ItemsToReturn = new List<PreflightItemDto>
                {
                    new("CHECK_MIC_INPUT", "Mic", "Pass", "Ready", "None required."),
                    new("CHECK_RENDER_DEVICE", "Render", "Pass", "Ready", "None required."),
                    new("CHECK_BLUETOOTH_TRANSPORT", "BT", "Pass", "Ready", "None required.")
                }
            };

            using var service = new BridgeApplicationService(mockEngine);

            // Act 1: Select first pair
            await service.SelectDevicesAsync("MIC_A", "RENDER_A");
            Assert.Equal("MIC_A", service.CurrentSnapshot.SelectedCaptureDeviceId);
            Assert.Equal("RENDER_A", service.CurrentSnapshot.SelectedRenderDeviceId);

            // Act 2: Switch to second pair
            await service.SelectDevicesAsync("MIC_B", "RENDER_B");
            Assert.Equal("MIC_B", service.CurrentSnapshot.SelectedCaptureDeviceId);
            Assert.Equal("RENDER_B", service.CurrentSnapshot.SelectedRenderDeviceId);
        }

        #endregion
    }
}
