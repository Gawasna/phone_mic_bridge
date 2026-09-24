using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using PhoneMicBridge.Application;
using PhoneMicBridge.Devices;
using Xunit;

namespace PhoneMicBridge.Tests
{
    public class SubToolbarRouteAndBluetoothTests
    {
        #region Test Mocks & Helpers

        private class MockBridgeService : IBridgeApplicationService
        {
            public BridgeUiSnapshot CurrentSnapshot { get; set; } = BridgeUiSnapshot.Initial();
#pragma warning disable CS0067
            public event EventHandler<BridgeUiSnapshot>? SnapshotChanged;
#pragma warning restore CS0067

            public List<AudioDeviceDto> CaptureList { get; set; } = new();
            public List<AudioDeviceDto> RenderList { get; set; } = new();
            public List<BluetoothDeviceDto> BluetoothList { get; set; } = new();
            public bool BluetoothEnabled { get; set; } = true;
            public bool OpenSettingsCalled { get; set; }

            public AudioMeterValues GetCurrentMeterValues() => new(0f, 0f, -60f, 0f, 0f, -60f, false, false);
            public Task<BridgeUiSnapshot> RunPreflightDiagnosticsAsync(System.Threading.CancellationToken cancellationToken = default) => Task.FromResult(CurrentSnapshot);
            public Task<IReadOnlyList<AudioDeviceDto>> GetCaptureDevicesAsync() => Task.FromResult<IReadOnlyList<AudioDeviceDto>>(CaptureList);
            public Task<IReadOnlyList<AudioDeviceDto>> GetRenderDevicesAsync() => Task.FromResult<IReadOnlyList<AudioDeviceDto>>(RenderList);
            public Task<IReadOnlyList<BluetoothDeviceDto>> GetBluetoothDevicesAsync() => Task.FromResult<IReadOnlyList<BluetoothDeviceDto>>(BluetoothList);
            public bool IsBluetoothRadioEnabled() => BluetoothEnabled;
            public void OpenBluetoothSettings() => OpenSettingsCalled = true;

            public Task SelectDevicesAsync(string? captureDeviceId, string? renderDeviceId) => Task.CompletedTask;
            public Task StartBridgeAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StopBridgeAsync(System.Threading.CancellationToken cancellationToken = default) => Task.CompletedTask;
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
                ?? throw new InvalidOperationException("MainViewModel type not found.");

            return Activator.CreateInstance(vmType, service)!;
        }

        private static T GetProperty<T>(object instance, string propertyName)
        {
            var prop = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"Property '{propertyName}' not found on '{instance.GetType().FullName}'.");
            return (T)prop.GetValue(instance)!;
        }

        private static void SetProperty(object instance, string propertyName, object? value)
        {
            var prop = instance.GetType().GetProperty(propertyName, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"Property '{propertyName}' not found on '{instance.GetType().FullName}'.");
            prop.SetValue(instance, value);
        }

        private static void InvokeMethod(object instance, string methodName, object[]? args = null)
        {
            var method = instance.GetType().GetMethod(methodName, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"Method '{methodName}' not found on '{instance.GetType().FullName}'.");
            method.Invoke(instance, args);
        }

        private static async Task InvokeLoadDevicesAsync(object vm)
        {
            var method = vm.GetType().GetMethod("LoadDevicesAsync", BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException("Method LoadDevicesAsync not found on MainViewModel.");
            var task = (Task)method.Invoke(vm, null)!;
            await task;
        }

        #endregion

        #region 0. Live Host Discovery Inspection (Hardware Verification)

        [Fact]
        public void LiveHostDiscovery_Inspection()
        {
            var isBtEnabled = BluetoothDeviceDiscovery.IsBluetoothRadioEnabled();
            var btDevices = BluetoothDeviceDiscovery.DiscoverBluetoothDevices();
            var captures = DeviceDiscovery.GetCaptureDevices();
            var renders = DeviceDiscovery.GetRenderDevices();

            Console.WriteLine("\n================ LIVE DISCOVERY INSPECTION REPORT ================");
            Console.WriteLine($"[BLUETOOTH RADIO ENABLED]: {isBtEnabled}");
            Console.WriteLine($"\n[DISCOVERED BLUETOOTH DEVICES ({btDevices.Count})]:");
            foreach (var bt in btDevices)
            {
                Console.WriteLine($"  * Name: {bt.Name,-30} | Cat: {bt.Category,-7} | Conn: {bt.ConnectionStatus,-12} | Bridge: {bt.BridgeSupportStatus,-20} | Ready: {bt.IsBridgeReady} | CanSelect: {bt.CanSelectForBridge}");
            }

            Console.WriteLine($"\n[HOST CAPTURE (IN) DEVICES ({captures.Count})]:");
            foreach (var c in captures)
            {
                Console.WriteLine($"  * [{c.Index}] {c.Name,-50} | State: {c.State}");
            }

            Console.WriteLine($"\n[HOST RENDER (OUT) DEVICES ({renders.Count})]:");
            foreach (var r in renders)
            {
                Console.WriteLine($"  * [{r.Index}] {r.Name,-50} | State: {r.State,-10} | TargetHfp: {r.IsTargetHfp}");
            }
            Console.WriteLine("==================================================================\n");

            Assert.NotNull(btDevices);
            Assert.NotNull(captures);
            Assert.NotNull(renders);
        }

        [Fact]
        public async Task LiveHost_ViewModel_EndpointDevices_SensibilityInspection()
        {
            // Test real BridgeApplicationService against real Windows Audio subsystem
            using var realService = new BridgeApplicationService();
            var vm = CreateMainViewModel(realService);
            await InvokeLoadDevicesAsync(vm);

            // 1. Inspect Dropdown 1 (Bluetooth Devices)
            var btDevices = (ICollection)GetProperty<object>(vm, "BluetoothDevices");
            Console.WriteLine($"\n[VIEWMODEL REAL HOST BLUETOOTH DROPDOWN ({btDevices.Count} items)]:");
            foreach (var item in btDevices)
            {
                var bt = (BluetoothDeviceDto)item;
                Console.WriteLine($"  -> Name: {bt.Name,-25} | Status: {bt.ConnectionStatus,-10} | Bridge: {bt.BridgeSupportStatus}");
            }

            // 2. Inspect Dropdown 2 in Mode IN
            var inDevices = (ICollection)GetProperty<object>(vm, "AvailableEndpointDevices");
            Console.WriteLine($"\n[VIEWMODEL REAL HOST DROPDOWN 2 - MODE 'IN' ({inDevices.Count} items)]:");
            foreach (var item in inDevices)
            {
                var dev = (AudioDeviceDto)item;
                Console.WriteLine($"  -> {dev.Name,-50} | Active: {dev.IsActive} | Default: {dev.IsDefault}");
            }

            // 3. Switch to Mode OUT
            InvokeMethod(vm, "SetRouteMode", new object[] { BridgeRouteMode.Out });
            var outDevices = (ICollection)GetProperty<object>(vm, "AvailableEndpointDevices");
            var selectedAfterSwitch = GetProperty<object?>(vm, "SelectedEndpointDevice");

            Console.WriteLine($"\n[VIEWMODEL REAL HOST DROPDOWN 2 - MODE 'OUT' ({outDevices.Count} items)]:");
            foreach (var item in outDevices)
            {
                var dev = (AudioDeviceDto)item;
                Console.WriteLine($"  -> {dev.Name,-50} | Active: {dev.IsActive} | Default: {dev.IsDefault}");
            }

            Console.WriteLine($"\n[INVARIANT VERIFICATION]: SelectedEndpointDevice after IN->OUT switch: {(selectedAfterSwitch == null ? "NULL (PASS - None)" : "NOT NULL (FAIL)")}");
            Assert.Null(selectedAfterSwitch);
        }

        #endregion

        #region 1. Bluetooth Discovery Tests (5 Verification Gates)

        [Fact]
        public void DiscoverBluetoothDevices_WithHfpDevice_IdentifiesAsHfpReadyAndConnected()
        {
            var fakeRenders = new List<AudioDeviceInfo>
            {
                new(0, "dev-1", "Redmi Note 13 5G Hands-Free AG Audio", DeviceState.Active, IsTargetHfp: true),
                new(1, "dev-2", "Realtek High Definition Audio", DeviceState.Active, IsTargetHfp: false)
            };

            var fakeCaptures = new List<AudioDeviceInfo>
            {
                new(0, "dev-3", "Redmi Note 13 5G Hands-Free AG Audio", DeviceState.Active, IsTargetHfp: true)
            };

            var fakeRegistry = new Dictionary<string, string>
            {
                ["7f8a937194aa"] = "Redmi Note 13 5G"
            };

            var discovered = BluetoothDeviceDiscovery.DiscoverBluetoothDevices(
                renderProvider: () => fakeRenders,
                captureProvider: () => fakeCaptures,
                radioStateProvider: () => true,
                pairedRegistryProvider: () => fakeRegistry
            );

            Assert.Single(discovered);
            var btDev = discovered[0];
            Assert.Contains("Redmi Note 13", btDev.Name);
            Assert.Equal(BluetoothDeviceCategory.Phone, btDev.Category);
            Assert.True(btDev.IsConnected);
            Assert.True(btDev.IsHfpSupported);
            Assert.True(btDev.IsBridgeReady);
            Assert.True(btDev.CanSelectForBridge);
            Assert.Equal("READY TO BRIDGE", btDev.BridgeSupportStatus);
        }

        [Fact]
        public void DiscoverBluetoothDevices_WithA2dpOnlyDevice_IdentifiesAsIncompatible()
        {
            var fakeRenders = new List<AudioDeviceInfo>
            {
                new(0, "dev-a2dp", "Bluetooth Music Stereo", DeviceState.Active, IsTargetHfp: false)
            };

            var fakeCaptures = new List<AudioDeviceInfo>();

            var fakeRegistry = new Dictionary<string, string>
            {
                ["4142b28d63ef"] = "Bluetooth Music"
            };

            var discovered = BluetoothDeviceDiscovery.DiscoverBluetoothDevices(
                renderProvider: () => fakeRenders,
                captureProvider: () => fakeCaptures,
                radioStateProvider: () => true,
                pairedRegistryProvider: () => fakeRegistry
            );

            Assert.Single(discovered);
            var btDev = discovered[0];
            Assert.Contains("Bluetooth Music", btDev.Name);
            Assert.Equal(BluetoothDeviceCategory.Speaker, btDev.Category);
            Assert.True(btDev.IsConnected);
            Assert.False(btDev.IsHfpSupported);
            Assert.False(btDev.IsBridgeReady);
            Assert.False(btDev.CanSelectForBridge); // Gate 5 Selection Guard
            Assert.Equal("Incompatible (A2DP Only)", btDev.BridgeSupportStatus);
        }

        [Fact]
        public void DiscoverBluetoothDevices_OfflinePhoneInRegistry_IdentifiesAsHfpPhoneOffline()
        {
            // Phone exists in registry (Gate 1 paired), but NO audio endpoint is active/connected (Gate 2 offline)
            var fakeRenders = new List<AudioDeviceInfo>();
            var fakeCaptures = new List<AudioDeviceInfo>();

            var fakeRegistry = new Dictionary<string, string>
            {
                ["7f8a937194aa"] = "Redmi Note 13 5G"
            };

            var discovered = BluetoothDeviceDiscovery.DiscoverBluetoothDevices(
                renderProvider: () => fakeRenders,
                captureProvider: () => fakeCaptures,
                radioStateProvider: () => true,
                pairedRegistryProvider: () => fakeRegistry
            );

            Assert.Single(discovered);
            var btDev = discovered[0];
            Assert.Equal("Redmi Note 13 5G", btDev.Name);
            Assert.Equal(BluetoothDeviceCategory.Phone, btDev.Category);
            Assert.False(btDev.IsConnected); // Gate 2 Offline
            Assert.True(btDev.IsHfpSupported); // Gate 3 Phone has HFP capability
            Assert.False(btDev.IsBridgeReady); // Gate 4 Not ready
            Assert.False(btDev.CanSelectForBridge); // Gate 5 Cannot select when offline
            Assert.Contains("Offline", btDev.BridgeSupportStatus);
        }

        [Fact]
        public void DiscoverBluetoothDevices_WhenRadioDisabled_ReturnsEmptyList()
        {
            var fakeRenders = new List<AudioDeviceInfo>
            {
                new(0, "dev-hfp", "Redmi Note 13 5G Hands-Free", DeviceState.Active, IsTargetHfp: true)
            };

            var discovered = BluetoothDeviceDiscovery.DiscoverBluetoothDevices(
                renderProvider: () => fakeRenders,
                captureProvider: () => new List<AudioDeviceInfo>(),
                radioStateProvider: () => false // Radio disabled
            );

            Assert.Empty(discovered);
        }

        [Fact]
        public void BluetoothClassOfDevice_BitfieldDecoding_CompliesWithBluetoothSigAssignedNumbers()
        {
            // 1. Phone (Joy 3: 0x5A020C)
            // Major: 0x02 (Phone), Minor: 0x03 (Smartphone), Services: Telephony, Audio, Object Transfer, Networking
            var joyCod = new BluetoothClassOfDevice(0x5A020C);
            Assert.Equal(BluetoothClassOfDevice.MajorPhone, joyCod.MajorDeviceClass);
            Assert.Equal(BluetoothClassOfDevice.MinorPhoneSmartphone, joyCod.MinorDeviceClass);
            Assert.True(joyCod.IsConfirmedPhone);
            Assert.False(joyCod.IsConfirmedLoudspeaker);
            Assert.False(joyCod.IsConfirmedHeadset);
            Assert.True(joyCod.HasTelephonyService);
            Assert.False(joyCod.HasAudioService); // Bluetooth SIG Bit 21: Joy 3 advertises Telephony, not Audio
            Assert.True(joyCod.HasCapturingService); // Bit 19: Capturing

            // 2. Loudspeaker (Bluetooth Music: 0x240418)
            // Major: 0x04 (Audio/Video), Minor: 0x06 (Loudspeaker/Headphones), Services: Audio, Rendering
            var spkCod = new BluetoothClassOfDevice(0x240418);
            Assert.Equal(BluetoothClassOfDevice.MajorAudioVideo, spkCod.MajorDeviceClass);
            Assert.True(spkCod.IsConfirmedLoudspeaker);
            Assert.False(spkCod.IsConfirmedPhone);
            Assert.False(spkCod.HasTelephonyService);
            Assert.True(spkCod.HasAudioService);
            Assert.True(spkCod.HasRenderingService);

            // 3. Wearable Headset with Mic (SY-T25: 0x240404)
            // Major: 0x04 (Audio/Video), Minor: 0x01 (Wearable Headset), Services: Audio, Rendering
            var headsetCod = new BluetoothClassOfDevice(0x240404);
            Assert.Equal(BluetoothClassOfDevice.MajorAudioVideo, headsetCod.MajorDeviceClass);
            Assert.Equal(BluetoothClassOfDevice.MinorAudioWearableHeadset, headsetCod.MinorDeviceClass);
            Assert.True(headsetCod.IsConfirmedHeadset);
            Assert.False(headsetCod.IsConfirmedLoudspeaker);
            Assert.False(headsetCod.IsConfirmedPhone);
        }

        [Fact]
        public void DiscoverBluetoothDevices_3TierEvidence_ExplicitCodOverridesNamingHeuristic()
        {
            // Device with deceptive name "Portable Speaker System" but explicit CoD = 0x5A020C (Smartphone)
            var fakeRegistry = new Dictionary<string, PairedRegistryDevice>
            {
                ["112233445566"] = new PairedRegistryDevice("112233445566", "Portable Speaker System", Cod: 0x5A020C)
            };

            var discovered = BluetoothDeviceDiscovery.DiscoverBluetoothDevices(
                renderProvider: () => new List<AudioDeviceInfo>(),
                captureProvider: () => new List<AudioDeviceInfo>(),
                radioStateProvider: () => true,
                pairedRegistryProvider: () => fakeRegistry
            );

            Assert.Single(discovered);
            // Tier 1 explicit CoD must take precedence over the word "Speaker" in name!
            Assert.Equal(BluetoothDeviceCategory.Phone, discovered[0].Category);
            Assert.True(discovered[0].IsHfpSupported);
        }

        [Fact]
        public void DiscoverBluetoothDevices_3TierEvidence_BleWithoutCod_UsesPnpAudioEndpointRepresentation()
        {
            // BLE device with no CoD (null), but Windows CoreAudio PnP registered Hands-Free endpoint
            var fakeRegistry = new Dictionary<string, PairedRegistryDevice>
            {
                ["7f8a937194aa"] = new PairedRegistryDevice("7f8a937194aa", "Redmi Note 13 5G", Cod: null, LeAppearance: 0)
            };

            var fakeCaptures = new List<AudioDeviceInfo>
            {
                new(0, "pnp-mic", "Microphone (Redmi Note 13 5G Hands-Free HF Audio)", DeviceState.NotPresent, IsTargetHfp: true)
            };

            var discovered = BluetoothDeviceDiscovery.DiscoverBluetoothDevices(
                renderProvider: () => new List<AudioDeviceInfo>(),
                captureProvider: () => fakeCaptures,
                radioStateProvider: () => true,
                pairedRegistryProvider: () => fakeRegistry
            );

            Assert.Single(discovered);
            // Tier 2 PnP Audio Endpoint Representation confirms HFP Phone Target
            Assert.Equal(BluetoothDeviceCategory.Phone, discovered[0].Category);
            Assert.True(discovered[0].IsHfpSupported);
            Assert.False(discovered[0].IsConnected); // NotPresent -> Offline
            Assert.Equal("HFP Phone Target (Offline)", discovered[0].BridgeSupportStatus);
        }

        #endregion

        #region 2. SubToolbar Route Mode & Invariant Tests

        [Fact]
        public void InitialRouteMode_IsRouteIn()
        {
            var mockService = new MockBridgeService();
            var vm = CreateMainViewModel(mockService);

            var isRouteIn = GetProperty<bool>(vm, "IsRouteIn");
            var isRouteOut = GetProperty<bool>(vm, "IsRouteOut");
            var label = GetProperty<string>(vm, "Dropdown2Label");

            Assert.True(isRouteIn);
            Assert.False(isRouteOut);
            Assert.Equal("HOST IN:", label);
        }

        [Fact]
        public async Task SetRouteMode_FromInToOut_ResetsSelectedEndpointDeviceToNull()
        {
            var mockService = new MockBridgeService
            {
                CaptureList = new List<AudioDeviceDto>
                {
                    new("mic-1", "PC Internal Microphone", true, false, true)
                },
                RenderList = new List<AudioDeviceDto>
                {
                    new("spk-1", "PC Realtek Speakers", true, false, true)
                }
            };

            var vm = CreateMainViewModel(mockService);
            await InvokeLoadDevicesAsync(vm);

            // Initially in Mode IN, device is selected
            var initialDevice = GetProperty<object?>(vm, "SelectedEndpointDevice");
            Assert.NotNull(initialDevice);

            // Transition to OUT
            InvokeMethod(vm, "SetRouteMode", new object[] { BridgeRouteMode.Out });

            // INVARIANT ASSERTION: "default option mỗi khi chuyển qua lại giữa in out phải đặt về none để tránh gây lỗi"
            var resetDevice = GetProperty<object?>(vm, "SelectedEndpointDevice");
            Assert.Null(resetDevice);

            var isRouteOut = GetProperty<bool>(vm, "IsRouteOut");
            var label = GetProperty<string>(vm, "Dropdown2Label");
            Assert.True(isRouteOut);
            Assert.Equal("HOST OUT:", label);
        }

        [Fact]
        public async Task SetRouteMode_FromOutToIn_ResetsSelectedEndpointDeviceToNull()
        {
            var mockService = new MockBridgeService
            {
                CaptureList = new List<AudioDeviceDto>
                {
                    new("mic-1", "PC Internal Microphone", true, false, true)
                },
                RenderList = new List<AudioDeviceDto>
                {
                    new("spk-1", "PC Realtek Speakers", true, false, true)
                }
            };

            var vm = CreateMainViewModel(mockService);
            await InvokeLoadDevicesAsync(vm);

            // Set to OUT first
            InvokeMethod(vm, "SetRouteMode", new object[] { BridgeRouteMode.Out });
            Assert.Null(GetProperty<object?>(vm, "SelectedEndpointDevice"));

            // Manually pick a speaker in OUT mode
            var renderDevice = mockService.RenderList[0];
            SetProperty(vm, "SelectedEndpointDevice", renderDevice);
            Assert.NotNull(GetProperty<object?>(vm, "SelectedEndpointDevice"));

            // Switch back to IN
            InvokeMethod(vm, "SetRouteMode", new object[] { BridgeRouteMode.In });

            // INVARIANT ASSERTION: Must be reset to null again
            Assert.Null(GetProperty<object?>(vm, "SelectedEndpointDevice"));
            Assert.True(GetProperty<bool>(vm, "IsRouteIn"));
            Assert.Equal("HOST IN:", GetProperty<string>(vm, "Dropdown2Label"));
        }

        [Fact]
        public async Task SetRouteMode_SwitchesAvailableEndpointDevices_BetweenCaptureAndRender()
        {
            var mockService = new MockBridgeService
            {
                CaptureList = new List<AudioDeviceDto>
                {
                    new("mic-1", "PC Microphone A", true, false, true),
                    new("mic-2", "PC Microphone B", true, false, false)
                },
                RenderList = new List<AudioDeviceDto>
                {
                    new("spk-1", "PC Speaker 1", true, false, true)
                }
            };

            var vm = CreateMainViewModel(mockService);
            await InvokeLoadDevicesAsync(vm);

            // Mode IN -> AvailableEndpointDevices has 2 captures
            var availableIn = (ICollection)GetProperty<object>(vm, "AvailableEndpointDevices");
            Assert.Equal(2, availableIn.Count);

            // Mode OUT -> AvailableEndpointDevices has 1 render
            InvokeMethod(vm, "SetRouteMode", new object[] { BridgeRouteMode.Out });
            var availableOut = (ICollection)GetProperty<object>(vm, "AvailableEndpointDevices");
            Assert.Single(availableOut);
        }

        [Fact]
        public async Task LoadDevicesAsync_PopulatesBluetoothDevices()
        {
            var mockService = new MockBridgeService
            {
                BluetoothList = new List<BluetoothDeviceDto>
                {
                    new("bt-1", "Redmi Note 13 5G", "7F:8A:93:71:94:AA", BluetoothDeviceCategory.Phone, "Connected (Online)", "READY TO BRIDGE", true, true, true, true)
                }
            };

            var vm = CreateMainViewModel(mockService);
            await InvokeLoadDevicesAsync(vm);

            var btDevices = (ICollection)GetProperty<object>(vm, "BluetoothDevices");
            Assert.Single(btDevices);

            var selectedBt = GetProperty<BluetoothDeviceDto?>(vm, "SelectedBluetoothDevice");
            Assert.NotNull(selectedBt);
            Assert.Equal("Redmi Note 13 5G", selectedBt.Name);
        }

        [Fact]
        public void SelectionGuard_MainViewModel_RejectsUnbridgeableDevice()
        {
            var mockService = new MockBridgeService();
            var vm = CreateMainViewModel(mockService);

            var offlineDev = new BluetoothDeviceDto(
                "dev-offline",
                "Redmi Note 13 5G",
                "7F:8A:93:71:94:AA",
                BluetoothDeviceCategory.Phone,
                "Offline (Disconnected)",
                "HFP Phone Target (Offline)",
                false,
                true,
                false,
                false // CanSelectForBridge == false
            );

            // Gate 5: Attempting to select an unbridgeable/offline device must be rejected
            SetProperty(vm, "SelectedBluetoothDevice", offlineDev);
            var currentSelected = GetProperty<object?>(vm, "SelectedBluetoothDevice");
            Assert.Null(currentSelected);

            // Now provide a device that passed Gate 5 (CanSelectForBridge == true)
            var readyDev = new BluetoothDeviceDto(
                "dev-ready",
                "Redmi Note 13 5G",
                "7F:8A:93:71:94:AA",
                BluetoothDeviceCategory.Phone,
                "Connected (Online)",
                "READY TO BRIDGE",
                true,
                true,
                true,
                true // CanSelectForBridge == true
            );

            SetProperty(vm, "SelectedBluetoothDevice", readyDev);
            var selectedReady = GetProperty<BluetoothDeviceDto?>(vm, "SelectedBluetoothDevice");
            Assert.NotNull(selectedReady);
            Assert.Equal("Redmi Note 13 5G", selectedReady.Name);
        }

        #endregion

        #region 4. Canonical Physical Device Aggregation Tests (8 Acceptance Criteria)

        [Fact]
        public void CanonicalAggregation_IdenticalContainerId_AggregatesIntoSingleCanonicalDevice_SatisfiesAC1ToAC4()
        {
            // AC 1: DRM25 + Redmi Note 13 5G -> 1 canonical device
            // AC 2: Classic/BLE identities preserved inside canonical device
            // AC 3: HFP capability from Classic preserved
            // AC 4: BLE metadata preserved
            const string sharedContainerId = "{4a9ee693-30f2-5e19-90c3-f4d994145941}";

            var classicRegistryDev = new PairedRegistryDevice(
                Mac: "d4a365e826a3",
                Name: "DRM25",
                Cod: 0x5A020C, // Phone (Major 0x02, Minor Smartphone 0x03) + Audio/Telephony
                LeAppearance: null,
                BoundSubKeys: new List<string>(),
                ContainerId: sharedContainerId
            );

            var bleRegistryDev = new PairedRegistryDevice(
                Mac: "7f8a937194aa",
                Name: "Redmi Note 13 5G",
                Cod: null,
                LeAppearance: 0x03C1,
                BoundSubKeys: new List<string> { "LE" },
                ContainerId: sharedContainerId
            );

            var pairedDevices = new Dictionary<string, PairedRegistryDevice>(StringComparer.OrdinalIgnoreCase)
            {
                ["d4a365e826a3"] = classicRegistryDev,
                ["7f8a937194aa"] = bleRegistryDev
            };

            var discovered = BluetoothDeviceDiscovery.DiscoverBluetoothDevices(
                renderProvider: () => new List<AudioDeviceInfo>(),
                captureProvider: () => new List<AudioDeviceInfo>(),
                radioStateProvider: () => true,
                pairedRegistryProvider: () => pairedDevices
            );

            // AC 1: Exactly 1 canonical device
            Assert.Single(discovered);
            var canonical = discovered[0];

            // Primary name + alias
            Assert.Equal("DRM25 (Redmi Note 13 5G)", canonical.Name);
            Assert.Equal(sharedContainerId, canonical.ContainerId);
            Assert.Equal(BluetoothDeviceCategory.Phone, canonical.Category);

            // AC 2: Both Classic & BLE identities preserved
            Assert.NotNull(canonical.TransportIdentities);
            Assert.Equal(2, canonical.TransportIdentities.Count);

            var classicTransport = canonical.TransportIdentities.FirstOrDefault(t => t.TransportType == BluetoothTransportType.Classic);
            Assert.NotNull(classicTransport);
            Assert.Equal("D4:A3:65:E8:26:A3", classicTransport.MacAddress);
            Assert.Equal("DRM25", classicTransport.AdvertisedName);
            Assert.Equal(0x5A020C, classicTransport.Cod);

            var bleTransport = canonical.TransportIdentities.FirstOrDefault(t => t.TransportType == BluetoothTransportType.Ble);
            Assert.NotNull(bleTransport);
            Assert.Equal("7F:8A:93:71:94:AA", bleTransport.MacAddress);
            Assert.Equal("Redmi Note 13 5G", bleTransport.AdvertisedName);

            // AC 3: HFP capability from Classic is preserved
            Assert.True(canonical.IsHfpSupported);
            Assert.True(classicTransport.HasHfpProfile);

            // AC 4: BLE metadata is preserved
            Assert.Equal(0x03C1, bleTransport.LeAppearance);
        }

        [Fact]
        public void CanonicalAggregation_DifferentContainerId_NeverMergesDespiteIdenticalName_SatisfiesAC5AndAC6()
        {
            // AC 5: Device khác ContainerId -> không merge
            // AC 6: Không merge chỉ dựa vào Name/MAC similarity
            var dev1 = new PairedRegistryDevice(
                Mac: "112233445566",
                Name: "BT 5.0",
                Cod: 0x240404,
                LeAppearance: null,
                BoundSubKeys: new List<string>(),
                ContainerId: "{11111111-1111-1111-1111-111111111111}"
            );

            var dev2 = new PairedRegistryDevice(
                Mac: "998877665544",
                Name: "BT 5.0",
                Cod: 0x240404,
                LeAppearance: null,
                BoundSubKeys: new List<string>(),
                ContainerId: "{22222222-2222-2222-2222-222222222222}"
            );

            var pairedDevices = new Dictionary<string, PairedRegistryDevice>(StringComparer.OrdinalIgnoreCase)
            {
                ["112233445566"] = dev1,
                ["998877665544"] = dev2
            };

            var discovered = BluetoothDeviceDiscovery.DiscoverBluetoothDevices(
                renderProvider: () => new List<AudioDeviceInfo>(),
                captureProvider: () => new List<AudioDeviceInfo>(),
                radioStateProvider: () => true,
                pairedRegistryProvider: () => pairedDevices
            );

            // Must NOT merge: exactly 2 distinct devices despite identical name
            Assert.Equal(2, discovered.Count);
            Assert.NotEqual(discovered[0].ContainerId, discovered[1].ContainerId);
        }

        [Fact]
        public void CanonicalAggregation_NullContainerId_NeverMerges()
        {
            // Devices without ContainerId must NEVER merge
            var dev1 = new PairedRegistryDevice(
                Mac: "aabbcc112233",
                Name: "Generic Audio",
                Cod: 0x240404,
                ContainerId: null
            );

            var dev2 = new PairedRegistryDevice(
                Mac: "ddeeff445566",
                Name: "Generic Audio",
                Cod: 0x240404,
                ContainerId: null
            );

            var pairedDevices = new Dictionary<string, PairedRegistryDevice>(StringComparer.OrdinalIgnoreCase)
            {
                ["aabbcc112233"] = dev1,
                ["ddeeff445566"] = dev2
            };

            var discovered = BluetoothDeviceDiscovery.DiscoverBluetoothDevices(
                renderProvider: () => new List<AudioDeviceInfo>(),
                captureProvider: () => new List<AudioDeviceInfo>(),
                radioStateProvider: () => true,
                pairedRegistryProvider: () => pairedDevices
            );

            Assert.Equal(2, discovered.Count);
        }

        [Fact]
        public void CanonicalAggregation_LiveHostInspection_SingleRedmiTarget_SatisfiesAC8()
        {
            // AC 8: Live host inspection displays exactly 1 Redmi target instead of 2
            var liveDiscovered = BluetoothDeviceDiscovery.DiscoverBluetoothDevices();

            var redmiMatches = liveDiscovered
                .Where(d => d.Category == BluetoothDeviceCategory.Phone &&
                           (d.Name.Contains("Redmi", StringComparison.OrdinalIgnoreCase) ||
                            d.Name.Contains("DRM25", StringComparison.OrdinalIgnoreCase)))
                .ToList();

            // AC 8: Exactly 1 Redmi phone target (DRM25 + Redmi Note 13 5G merged)
            Assert.Single(redmiMatches);

            var redmi = redmiMatches[0];
            Assert.Contains("DRM25", redmi.Name);
            Assert.Contains("Redmi Note 13 5G", redmi.Name);
            Assert.Equal(BluetoothDeviceCategory.Phone, redmi.Category);

            // Assert transport identities contain both Classic and BLE
            Assert.NotNull(redmi.TransportIdentities);
            Assert.True(redmi.TransportIdentities.Count >= 2);
            Assert.Contains(redmi.TransportIdentities, t => t.TransportType == BluetoothTransportType.Classic);
            Assert.Contains(redmi.TransportIdentities, t => t.TransportType == BluetoothTransportType.Ble);
        }

        #endregion
    }
}
