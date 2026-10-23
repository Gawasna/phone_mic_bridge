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
using PhoneMicBridge.Devices;
using Xunit;

namespace PhoneMicBridge.Tests
{
    /// <summary>
    /// Empirical Challenger Verification Test Suite for Milestone M2.
    /// Focuses on adversarial challenge and empirical stress-testing of:
    /// 1. Device selection and disambiguation with duplicate Friendly Names.
    /// 2. Selection persistence across device list reload cycles (reordering, additions).
    /// 3. Graceful recovery and fallback when selected devices disappear or disconnect.
    /// 4. Safety interlock gating and exception blocking under device disappearance.
    /// </summary>
    public class EmpiricalM2DeviceDisambiguationTests
    {
        #region Test Doubles & Reflection Helpers

        public class MockApplicationService : IBridgeApplicationService
        {
            public BridgeUiSnapshot CurrentSnapshot { get; set; } = BridgeUiSnapshot.Initial();
            public event EventHandler<BridgeUiSnapshot>? SnapshotChanged;
#pragma warning disable CS0067
        public event EventHandler<bool>? RadioStateChanged;
#pragma warning restore CS0067

            public List<AudioDeviceDto> CaptureDevicesList { get; set; } = new();
            public List<AudioDeviceDto> RenderDevicesList { get; set; } = new();

            public string? LastSelectedCaptureDeviceId { get; private set; }
            public string? LastSelectedRenderDeviceId { get; private set; }
            public int SelectDevicesCallCount { get; private set; }

            public AudioMeterValues GetCurrentMeterValues() => new(0f, 0f, -60f, 0f, 0f, -60f, false, false);

            public Task<BridgeUiSnapshot> RunPreflightDiagnosticsAsync(CancellationToken cancellationToken = default)
            {
                return Task.FromResult(CurrentSnapshot);
            }

            public Task<IReadOnlyList<AudioDeviceDto>> GetCaptureDevicesAsync()
            {
                return Task.FromResult<IReadOnlyList<AudioDeviceDto>>(CaptureDevicesList);
            }

            public Task<IReadOnlyList<AudioDeviceDto>> GetRenderDevicesAsync()
            {
                return Task.FromResult<IReadOnlyList<AudioDeviceDto>>(RenderDevicesList);
            }

            public Task SelectDevicesAsync(string? captureDeviceId, string? renderDeviceId)
            {
                LastSelectedCaptureDeviceId = captureDeviceId;
                LastSelectedRenderDeviceId = renderDeviceId;
                SelectDevicesCallCount++;

                CurrentSnapshot = CurrentSnapshot with
                {
                    SelectedCaptureDeviceId = captureDeviceId,
                    SelectedRenderDeviceId = renderDeviceId
                };
                SnapshotChanged?.Invoke(this, CurrentSnapshot);

                return Task.CompletedTask;
            }

            public Task StartBridgeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StopBridgeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void SetMicEnabled(bool enabled) { }
            public void SetMicGain(float gain) { }
            public Task<bool> LoadAudioFileAsync(string filePath, float initialGain = 0.7f) => Task.FromResult(true);
            public void ToggleFilePlayback() { }
            public void SetFileGain(float gain) { }
            public void SetToneEnabled(bool enabled, double frequency = 1000.0, float initialGain = 0.35f) { }
            public void SetToneGain(float gain) { }
            public void ClearError() { }
            public void Dispose() { }
        }

        private static AudioDeviceInfo CreateInfo(
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

        private static void SetProperty(object instance, string propertyName, object? value)
        {
            var prop = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"Property '{propertyName}' not found on type '{instance.GetType().FullName}'.");
            prop.SetValue(instance, value);
        }

        private static async Task InvokeLoadDevicesAsync(object vm)
        {
            var method = vm.GetType().GetMethod("LoadDevicesAsync", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException("Method LoadDevicesAsync not found on MainViewModel.");
            var task = (Task)method.Invoke(vm, null)!;
            await task;
        }

        #endregion

        #region 1. Empirical Verification: Duplicate Friendly Names Disambiguation

        [Fact]
        public async Task Empirical_DuplicateNames_CaptureSelection_RoutesExclusivelyByStableId_NotFriendlyName()
        {
            // Scenario: 3 microphones share the exact same Friendly Name "Realtek High Definition Audio",
            // but have different IDs and different operational states (Active, Disabled, Unplugged).
            const string duplicateFriendlyName = "Realtek High Definition Audio";
            string idActive = "{0.0.1.00000000}.{REALTEK_ACTIVE_ID}";
            string idDisabled = "{0.0.1.00000000}.{REALTEK_DISABLED_ID}";
            string idUnplugged = "{0.0.1.00000000}.{REALTEK_UNPLUGGED_ID}";

            var captureList = new List<AudioDeviceInfo>
            {
                CreateInfo(0, idDisabled, duplicateFriendlyName, DeviceState.Disabled),
                CreateInfo(1, idActive, duplicateFriendlyName, DeviceState.Active),
                CreateInfo(2, idUnplugged, duplicateFriendlyName, DeviceState.Unplugged)
            };

            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => captureList,
                renderDevicesProvider: () => new List<AudioDeviceInfo>()
            );

            // 1. Act: Select idActive specifically
            var resultsActive = await engine.ExecuteChecksAsync(captureDeviceId: idActive, renderDeviceId: null);
            var micItemActive = Assert.Single(resultsActive, r => r.CheckId == "CHECK_MIC_INPUT");

            // Assert: Must PASS because the selected device is active, regardless of earlier disabled devices with same name
            Assert.Equal("Pass", micItemActive.Status);
            Assert.Contains(duplicateFriendlyName, micItemActive.Details);

            // 2. Act: Select idDisabled specifically
            var resultsDisabled = await engine.ExecuteChecksAsync(captureDeviceId: idDisabled, renderDeviceId: null);
            var micItemDisabled = Assert.Single(resultsDisabled, r => r.CheckId == "CHECK_MIC_INPUT");

            // Assert: Must FAIL because idDisabled is disabled, even though an active device with identical name exists!
            Assert.Equal("Fail", micItemDisabled.Status);
            Assert.Contains("is not active (State: Disabled)", micItemDisabled.Details);
            Assert.Contains("Enable the microphone in Windows Sound Control Panel", micItemDisabled.RemediationText);

            // 3. Act: Select idUnplugged specifically
            var resultsUnplugged = await engine.ExecuteChecksAsync(captureDeviceId: idUnplugged, renderDeviceId: null);
            var micItemUnplugged = Assert.Single(resultsUnplugged, r => r.CheckId == "CHECK_MIC_INPUT");

            // Assert: Must FAIL because idUnplugged is unplugged
            Assert.Equal("Fail", micItemUnplugged.Status);
            Assert.Contains("is not active (State: Unplugged)", micItemUnplugged.Details);
        }

        [Fact]
        public async Task Empirical_DuplicateNames_RenderSelection_DifferentiatesHfpFromNonHfpWithIdenticalNames()
        {
            // Scenario: 2 output devices have the exact same Friendly Name "Bluetooth Audio Device",
            // but one is standard A2DP (IsTargetHfp = false) and one is Hands-Free (IsTargetHfp = true).
            const string sharedName = "Bluetooth Audio Device";
            string idA2dp = "{0.0.0.00000000}.{BT_A2DP_ENDPOINT}";
            string idHfp = "{0.0.0.00000000}.{BT_HFP_ENDPOINT}";

            var renderList = new List<AudioDeviceInfo>
            {
                CreateInfo(0, idA2dp, sharedName, DeviceState.Active, isTargetHfp: false),
                CreateInfo(1, idHfp, sharedName, DeviceState.Active, isTargetHfp: true)
            };

            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => new List<AudioDeviceInfo>(),
                renderDevicesProvider: () => renderList,
                bluetoothRadioStateProvider: () => true
            );

            // 1. Select idHfp
            var resultsHfp = await engine.ExecuteChecksAsync(captureDeviceId: null, renderDeviceId: idHfp);
            var renderItemHfp = Assert.Single(resultsHfp, r => r.CheckId == "CHECK_RENDER_DEVICE");
            var btItemHfp = Assert.Single(resultsHfp, r => r.CheckId == "CHECK_BLUETOOTH_TRANSPORT");

            Assert.Equal("Pass", renderItemHfp.Status);
            Assert.Equal("Pass", btItemHfp.Status);
            Assert.Contains("Bluetooth Hands-Free transport channel ready", btItemHfp.Details);

            // 2. Select idA2dp
            var resultsA2dp = await engine.ExecuteChecksAsync(captureDeviceId: null, renderDeviceId: idA2dp);
            var renderItemA2dp = Assert.Single(resultsA2dp, r => r.CheckId == "CHECK_RENDER_DEVICE");
            var btItemA2dp = Assert.Single(resultsA2dp, r => r.CheckId == "CHECK_BLUETOOTH_TRANSPORT");

            Assert.Equal("Pass", renderItemA2dp.Status);
            // Must FAIL Bluetooth check because A2DP endpoint cannot route SCO telephony audio!
            Assert.Equal("Fail", btItemA2dp.Status);
            Assert.Contains("not a Bluetooth Hands-Free endpoint", btItemA2dp.Details);
            Assert.Contains("Select a Bluetooth Hands-Free AG Audio device", btItemA2dp.RemediationText);
        }

        [Fact]
        public async Task Empirical_DuplicateNames_ViewModel_SelectionBindsAndDispatchesAccurateId()
        {
            // Scenario: MainViewModel receives duplicate named devices and user selects the 2nd one.
            var mockService = new MockApplicationService
            {
                CaptureDevicesList = new List<AudioDeviceDto>
                {
                    new("{0.0.1.00000000}.{DEV_ID_ALPHA}", "Realtek High Definition Audio", true, false, false),
                    new("{0.0.1.00000000}.{DEV_ID_BETA}", "Realtek High Definition Audio", true, false, false),
                    new("{0.0.1.00000000}.{DEV_ID_GAMMA}", "Realtek High Definition Audio", true, false, false)
                }
            };

            var vm = CreateMainViewModel(mockService);
            await InvokeLoadDevicesAsync(vm);

            var captureDevices = GetProperty<System.Collections.ObjectModel.ObservableCollection<AudioDeviceDto>>(vm, "CaptureDevices");
            Assert.Equal(3, captureDevices.Count);

            // Select the second device (BETA)
            var targetDevice = captureDevices.First(d => d.Id == "{0.0.1.00000000}.{DEV_ID_BETA}");
            SetProperty(vm, "SelectedCaptureDevice", targetDevice);

            // Assert SelectedCaptureDeviceId reflects BETA
            string? selectedId = GetProperty<string?>(vm, "SelectedCaptureDeviceId");
            Assert.Equal("{0.0.1.00000000}.{DEV_ID_BETA}", selectedId);

            // Allow async background dispatch to complete
            await Task.Delay(100);

            // Verify mock service received the exact ID BETA
            Assert.Equal("{0.0.1.00000000}.{DEV_ID_BETA}", mockService.LastSelectedCaptureDeviceId);
        }

        #endregion

        #region 2. Empirical Verification: Persistence Across Reload Cycles

        [Fact]
        public async Task Empirical_ReloadCycle_PreservesSelectedCaptureAndRenderIds_WhenStillPresent()
        {
            // Scenario: User selects a specific microphone and render endpoint.
            // A reload occurs where device list is reordered and new devices are inserted.
            // The selected device IDs must remain preserved and selected.
            string chosenMicId = "{0.0.1.00000000}.{MY_CHOSEN_MIC}";
            string chosenRenderId = "{0.0.0.00000000}.{MY_CHOSEN_RENDER}";

            var mockService = new MockApplicationService
            {
                CaptureDevicesList = new List<AudioDeviceDto>
                {
                    new("{0.0.1.00000000}.{MIC_1}", "Internal Mic", true, false, true),
                    new(chosenMicId, "USB Gaming Mic", true, false, false),
                    new("{0.0.1.00000000}.{MIC_3}", "Webcam Mic", true, false, false)
                },
                RenderDevicesList = new List<AudioDeviceDto>
                {
                    new("{0.0.0.00000000}.{RENDER_1}", "Realtek Speakers", true, false, true),
                    new(chosenRenderId, "Pixel 8 Hands-Free AG Audio", true, true, false)
                }
            };

            var vm = CreateMainViewModel(mockService);
            await InvokeLoadDevicesAsync(vm);

            // Select chosen devices
            var captureDevices = GetProperty<System.Collections.ObjectModel.ObservableCollection<AudioDeviceDto>>(vm, "CaptureDevices");
            var renderDevices = GetProperty<System.Collections.ObjectModel.ObservableCollection<AudioDeviceDto>>(vm, "RenderDevices");

            SetProperty(vm, "SelectedCaptureDevice", captureDevices.First(c => c.Id == chosenMicId));
            SetProperty(vm, "SelectedRenderDevice", renderDevices.First(r => r.Id == chosenRenderId));

            Assert.Equal(chosenMicId, GetProperty<string?>(vm, "SelectedCaptureDeviceId"));
            Assert.Equal(chosenRenderId, GetProperty<string?>(vm, "SelectedRenderDeviceId"));

            // Now mutate device list: reverse order, prepend brand new devices
            mockService.CaptureDevicesList = new List<AudioDeviceDto>
            {
                new("{0.0.1.00000000}.{NEW_MIC_X}", "Brand New Mic", true, false, false),
                new("{0.0.1.00000000}.{MIC_3}", "Webcam Mic", true, false, false),
                new(chosenMicId, "USB Gaming Mic", true, false, false), // still present
                new("{0.0.1.00000000}.{MIC_1}", "Internal Mic", true, false, true)
            };
            mockService.RenderDevicesList = new List<AudioDeviceDto>
            {
                new("{0.0.0.00000000}.{NEW_RENDER_Y}", "New Headphones", true, true, false),
                new(chosenRenderId, "Pixel 8 Hands-Free AG Audio", true, true, false), // still present
                new("{0.0.0.00000000}.{RENDER_1}", "Realtek Speakers", true, false, true)
            };

            // Trigger reload cycle
            await InvokeLoadDevicesAsync(vm);

            // Assert: Selected IDs MUST be preserved exactly
            Assert.Equal(chosenMicId, GetProperty<string?>(vm, "SelectedCaptureDeviceId"));
            Assert.Equal(chosenRenderId, GetProperty<string?>(vm, "SelectedRenderDeviceId"));

            var currentCap = GetProperty<AudioDeviceDto?>(vm, "SelectedCaptureDevice");
            var currentRen = GetProperty<AudioDeviceDto?>(vm, "SelectedRenderDevice");
            Assert.NotNull(currentCap);
            Assert.Equal(chosenMicId, currentCap.Id);
            Assert.NotNull(currentRen);
            Assert.Equal(chosenRenderId, currentRen.Id);
        }

        [Fact]
        public async Task Empirical_ReloadCycle_WithDuplicateNames_PreservesExactStableIdAcrossReordering()
        {
            // Scenario: 3 devices all named "Realtek High Definition Audio".
            // User selects ID_03. List is reloaded in reverse order where ID_03 is at the front,
            // or scrambled. The selection must not shift to ID_01 or ID_02.
            string id1 = "{0.0.1.00000000}.{REALTEK_01}";
            string id2 = "{0.0.1.00000000}.{REALTEK_02}";
            string id3 = "{0.0.1.00000000}.{REALTEK_03}";
            const string dupName = "Realtek High Definition Audio";

            var mockService = new MockApplicationService
            {
                CaptureDevicesList = new List<AudioDeviceDto>
                {
                    new(id1, dupName, true, false, false),
                    new(id2, dupName, true, false, false),
                    new(id3, dupName, true, false, false)
                }
            };

            var vm = CreateMainViewModel(mockService);
            await InvokeLoadDevicesAsync(vm);

            var captureDevices = GetProperty<System.Collections.ObjectModel.ObservableCollection<AudioDeviceDto>>(vm, "CaptureDevices");
            SetProperty(vm, "SelectedCaptureDevice", captureDevices.First(c => c.Id == id2));
            Assert.Equal(id2, GetProperty<string?>(vm, "SelectedCaptureDeviceId"));

            // Reload with reversed order
            mockService.CaptureDevicesList = new List<AudioDeviceDto>
            {
                new(id3, dupName, true, false, false),
                new(id2, dupName, true, false, false),
                new(id1, dupName, true, false, false)
            };

            await InvokeLoadDevicesAsync(vm);

            // Selection MUST remain id2, not id3 (first item) or id1
            Assert.Equal(id2, GetProperty<string?>(vm, "SelectedCaptureDeviceId"));
        }

        [Fact]
        public async Task Empirical_ReloadCycle_CaseInsensitiveStableIdMatch_PreservesSelection()
        {
            // Scenario: MMDevice IDs from Windows might vary in hex casing (e.g. lowercase vs uppercase).
            // MainViewModel must match with OrdinalIgnoreCase.
            string lowerId = "{0.0.1.00000000}.{abcdef12-3456-7890}";
            string upperId = "{0.0.1.00000000}.{ABCDEF12-3456-7890}";

            var mockService = new MockApplicationService
            {
                CaptureDevicesList = new List<AudioDeviceDto>
                {
                    new(lowerId, "Microphone", true, false, false)
                }
            };

            var vm = CreateMainViewModel(mockService);
            await InvokeLoadDevicesAsync(vm);

            var captureDevices = GetProperty<System.Collections.ObjectModel.ObservableCollection<AudioDeviceDto>>(vm, "CaptureDevices");
            SetProperty(vm, "SelectedCaptureDevice", captureDevices.First());

            // Reload with uppercase ID
            mockService.CaptureDevicesList = new List<AudioDeviceDto>
            {
                new(upperId, "Microphone", true, false, false)
            };

            await InvokeLoadDevicesAsync(vm);

            // Must match and update without falling back to null
            Assert.NotNull(GetProperty<AudioDeviceDto?>(vm, "SelectedCaptureDevice"));
            Assert.Equal(upperId, GetProperty<string?>(vm, "SelectedCaptureDeviceId"));
        }

        #endregion

        #region 3. Empirical Verification: Recovery When Selected Device Disappears

        [Fact]
        public async Task Empirical_DeviceDisappearance_SelectedCaptureDisappears_FallsBackToDefaultSafelyWithoutCrash()
        {
            // Scenario: Selected capture mic is unplugged. On reload, the system safely falls back
            // to the default or active mic without crashing.
            string usbMicId = "{0.0.1.00000000}.{USB_MIC_DISAPPEARING}";
            string defaultMicId = "{0.0.1.00000000}.{DEFAULT_INTERNAL_MIC}";

            var mockService = new MockApplicationService
            {
                CaptureDevicesList = new List<AudioDeviceDto>
                {
                    new(defaultMicId, "Internal Mic", true, false, true),
                    new(usbMicId, "USB Mic", true, false, false)
                }
            };

            var vm = CreateMainViewModel(mockService);
            await InvokeLoadDevicesAsync(vm);

            var captureDevices = GetProperty<System.Collections.ObjectModel.ObservableCollection<AudioDeviceDto>>(vm, "CaptureDevices");
            SetProperty(vm, "SelectedCaptureDevice", captureDevices.First(c => c.Id == usbMicId));
            Assert.Equal(usbMicId, GetProperty<string?>(vm, "SelectedCaptureDeviceId"));

            // USB mic is disconnected! Remaining list has only defaultMicId
            mockService.CaptureDevicesList = new List<AudioDeviceDto>
            {
                new(defaultMicId, "Internal Mic", true, false, true)
            };

            // Act: Reload
            await InvokeLoadDevicesAsync(vm);

            // Assert: Must fallback safely to defaultMicId without throwing
            Assert.Equal(defaultMicId, GetProperty<string?>(vm, "SelectedCaptureDeviceId"));
            var current = GetProperty<AudioDeviceDto?>(vm, "SelectedCaptureDevice");
            Assert.NotNull(current);
            Assert.Equal(defaultMicId, current.Id);
            Assert.True(current.IsDefault);
        }

        [Fact]
        public async Task Empirical_DeviceDisappearance_SelectedRenderDisappears_FallsBackToAlternateHfpSafelyWithoutCrash()
        {
            // Scenario: Selected HFP render device is powered off. On reload, system falls back
            // to an alternate active HFP device.
            string hfp1Id = "{0.0.0.00000000}.{HFP_PHONE_1}";
            string hfp2Id = "{0.0.0.00000000}.{HFP_PHONE_2}";
            string speakerId = "{0.0.0.00000000}.{SPEAKERS}";

            var mockService = new MockApplicationService
            {
                RenderDevicesList = new List<AudioDeviceDto>
                {
                    new(speakerId, "Speakers", true, false, false),
                    new(hfp1Id, "Galaxy S23 Hands-Free", true, true, false),
                    new(hfp2Id, "Pixel 8 Hands-Free", true, true, false)
                }
            };

            var vm = CreateMainViewModel(mockService);
            await InvokeLoadDevicesAsync(vm);

            var renderDevices = GetProperty<System.Collections.ObjectModel.ObservableCollection<AudioDeviceDto>>(vm, "RenderDevices");
            SetProperty(vm, "SelectedRenderDevice", renderDevices.First(r => r.Id == hfp1Id));
            Assert.Equal(hfp1Id, GetProperty<string?>(vm, "SelectedRenderDeviceId"));

            // hfp1 disconnected!
            mockService.RenderDevicesList = new List<AudioDeviceDto>
            {
                new(speakerId, "Speakers", true, false, false),
                new(hfp2Id, "Pixel 8 Hands-Free", true, true, false)
            };

            // Act: Reload
            await InvokeLoadDevicesAsync(vm);

            // Assert: Falls back to hfp2 (preferred HFP active device)
            Assert.Equal(hfp2Id, GetProperty<string?>(vm, "SelectedRenderDeviceId"));
        }

        [Fact]
        public async Task Empirical_DeviceDisappearance_AllDevicesDisappear_EntersSafeWarningStateWithoutCrash()
        {
            // Extreme boundary: All capture and render devices are removed from the system.
            var mockService = new MockApplicationService
            {
                CaptureDevicesList = new List<AudioDeviceDto>
                {
                    new("{0.0.1.00000000}.{MIC_1}", "Mic", true, false, true)
                },
                RenderDevicesList = new List<AudioDeviceDto>
                {
                    new("{0.0.0.00000000}.{RENDER_1}", "Render", true, true, false)
                }
            };

            var vm = CreateMainViewModel(mockService);
            await InvokeLoadDevicesAsync(vm);

            // Wipe out all devices
            mockService.CaptureDevicesList = new List<AudioDeviceDto>();
            mockService.RenderDevicesList = new List<AudioDeviceDto>();

            // Act: Reload
            await InvokeLoadDevicesAsync(vm);

            // Assert: Safe empty state, zero exceptions thrown
            Assert.Null(GetProperty<string?>(vm, "SelectedCaptureDeviceId"));
            Assert.Null(GetProperty<string?>(vm, "SelectedRenderDeviceId"));
            Assert.Null(GetProperty<AudioDeviceDto?>(vm, "SelectedCaptureDevice"));
            Assert.Null(GetProperty<AudioDeviceDto?>(vm, "SelectedRenderDevice"));

            var captureDevices = GetProperty<System.Collections.ObjectModel.ObservableCollection<AudioDeviceDto>>(vm, "CaptureDevices");
            var renderDevices = GetProperty<System.Collections.ObjectModel.ObservableCollection<AudioDeviceDto>>(vm, "RenderDevices");
            Assert.Empty(captureDevices);
            Assert.Empty(renderDevices);
        }

        [Fact]
        public async Task Empirical_DeviceDisappearance_PreflightAndService_GatingBlocksStartSessionWhenDeviceMissing()
        {
            // Scenario: Selected device ID is not present in hardware discovery.
            // Preflight must mark status as Fail, and BridgeApplicationService must block StartBridgeAsync.
            string missingCaptureId = "{0.0.1.00000000}.{NON_EXISTENT_MIC}";
            string missingRenderId = "{0.0.0.00000000}.{NON_EXISTENT_RENDER}";

            var engine = new PreflightDiagnosticsEngine(
                captureDevicesProvider: () => new List<AudioDeviceInfo>
                {
                    CreateInfo(0, "{0.0.1.00000000}.{OTHER_MIC}", "Built-in Mic", DeviceState.Active)
                },
                renderDevicesProvider: () => new List<AudioDeviceInfo>
                {
                    CreateInfo(0, "{0.0.0.00000000}.{OTHER_RENDER}", "Speakers", DeviceState.Active, isTargetHfp: false)
                }
            );

            // 1. Run engine directly with missing IDs
            var checks = await engine.ExecuteChecksAsync(missingCaptureId, missingRenderId);

            var micCheck = Assert.Single(checks, c => c.CheckId == "CHECK_MIC_INPUT");
            var renderCheck = Assert.Single(checks, c => c.CheckId == "CHECK_RENDER_DEVICE");
            var btCheck = Assert.Single(checks, c => c.CheckId == "CHECK_BLUETOOTH_TRANSPORT");

            Assert.Equal("Fail", micCheck.Status);
            Assert.Contains(missingCaptureId, micCheck.Details);
            Assert.Contains("select another active microphone", micCheck.RemediationText);

            Assert.Equal("Fail", renderCheck.Status);
            Assert.Contains(missingRenderId, renderCheck.Details);
            Assert.Contains("Rescan devices or pair and connect", renderCheck.RemediationText);

            Assert.Equal("Fail", btCheck.Status);

            // 2. Integration with BridgeApplicationService
            using var service = new BridgeApplicationService(engine);
            await service.SelectDevicesAsync(missingCaptureId, missingRenderId);

            Assert.Equal(DiagnosticOverallStatus.Fail, service.CurrentSnapshot.PreflightOverall);
            Assert.False(service.CurrentSnapshot.CanStartSession);

            // Calling StartBridgeAsync must be blocked with InvalidOperationException
            var ex = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await service.StartBridgeAsync();
            });
            Assert.Contains("Pre-flight diagnostics failed", ex.Message);
        }

        #endregion
    }
}

