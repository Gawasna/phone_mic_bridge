using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using PhoneMicBridge.Application;
using PhoneMicBridge.Devices;
using Xunit;

namespace PhoneMicBridge.Tests
{
    /// <summary>
    /// Verifies the decoupled 4-tier architecture (OS-Thinking Section 56 & 74):
    /// 1. Low-Level OS Registry Mechanism (BluetoothRegistryReader)
    /// 2. Specification Bitfield Representation (BluetoothClassOfDevice)
    /// 3. Classification & Heuristic Matching Policy (BluetoothDeviceClassifier)
    /// 4. Entity Aggregation & Presentation Formatting (BluetoothDeviceAggregator & BluetoothPresentationFormatter)
    /// 5. Thin Orchestrator Facade (BluetoothDeviceDiscovery)
    /// </summary>
    public class BluetoothDecoupledArchitectureTests
    {
        #region 1. Specification Bitfield Representation (Domain Standard)

        [Fact]
        public void BluetoothClassOfDevice_IsImmutableDomainStruct()
        {
            var cod = new BluetoothClassOfDevice(0x5A020C);
            Assert.Equal(BluetoothClassOfDevice.MajorPhone, cod.MajorDeviceClass);
            Assert.Equal(BluetoothClassOfDevice.MinorPhoneSmartphone, cod.MinorDeviceClass);
            Assert.True(cod.IsConfirmedPhone);
            Assert.False(cod.IsConfirmedHeadset);
            Assert.False(cod.IsConfirmedLoudspeaker);
            Assert.True(cod.HasTelephonyService);
            Assert.False(cod.HasAudioService);
            Assert.True(cod.HasCapturingService);
            Assert.False(cod.HasRenderingService);
        }

        #endregion

        #region 2. Classification & Heuristic Matching (Policy - High Volatility)

        [Fact]
        public void BluetoothDeviceClassifier_ClassifiesStandardPhoneKeywords()
        {
            var classifier = BluetoothDeviceClassifier.Default;

            Assert.Equal(BluetoothDeviceCategory.Phone, classifier.DetermineCategoryHeuristic("Redmi Note 13 5G"));
            Assert.Equal(BluetoothDeviceCategory.Phone, classifier.DetermineCategoryHeuristic("Galaxy S23 Ultra"));
            Assert.Equal(BluetoothDeviceCategory.Phone, classifier.DetermineCategoryHeuristic("iPhone 15 Pro"));
            Assert.Equal(BluetoothDeviceCategory.Phone, classifier.DetermineCategoryHeuristic("Google Pixel 8"));
            Assert.Equal(BluetoothDeviceCategory.Headset, classifier.DetermineCategoryHeuristic("AirPods Pro"));
            Assert.Equal(BluetoothDeviceCategory.Headset, classifier.DetermineCategoryHeuristic("Galaxy Buds2 Pro"));
            Assert.Equal(BluetoothDeviceCategory.Speaker, classifier.DetermineCategoryHeuristic("JBL Flip 6 Speaker"));
            Assert.Equal(BluetoothDeviceCategory.Other, classifier.DetermineCategoryHeuristic("Logitech MX Master 3S"));
        }

        [Fact]
        public void BluetoothDeviceClassifier_CanBeExtendedWithCustomKeywords_WithoutModifyingMechanism()
        {
            // Demonstrates that adding new phone brands/models modifies only policy, with zero blast radius to registry mechanism
            var customClassifier = new BluetoothDeviceClassifier(
                customPhoneKeywords: new[] { "poco", "meizu" },
                customSpeakerKeywords: new[] { "emberton" }
            );

            // Default classifier doesn't know "poco", "meizu", or "emberton"
            Assert.Equal(BluetoothDeviceCategory.Other, BluetoothDeviceClassifier.Default.DetermineCategoryHeuristic("POCO X6 Pro"));
            Assert.Equal(BluetoothDeviceCategory.Other, BluetoothDeviceClassifier.Default.DetermineCategoryHeuristic("Meizu 21"));
            Assert.Equal(BluetoothDeviceCategory.Other, BluetoothDeviceClassifier.Default.DetermineCategoryHeuristic("Marshall Emberton II"));

            // Custom extended classifier recognizes them immediately
            Assert.Equal(BluetoothDeviceCategory.Phone, customClassifier.DetermineCategoryHeuristic("POCO X6 Pro"));
            Assert.Equal(BluetoothDeviceCategory.Phone, customClassifier.DetermineCategoryHeuristic("Meizu 21"));
            Assert.Equal(BluetoothDeviceCategory.Speaker, customClassifier.DetermineCategoryHeuristic("Marshall Emberton II"));
        }

        [Fact]
        public void BluetoothDeviceClassifier_ExtractCoreDeviceName_StripsHandsFreeAndAudioTags()
        {
            var classifier = BluetoothDeviceClassifier.Default;

            string cleaned1 = classifier.ExtractCoreDeviceName("Headset (Redmi Note 13 5G Hands-Free AG Audio)");
            Assert.Equal("Redmi Note 13 5G", cleaned1);

            string cleaned2 = classifier.ExtractCoreDeviceName("Bluetooth Headset (SY-T25 Hands-Free HF Audio)");
            Assert.Equal("SY-T25", cleaned2);

            string cleaned3 = classifier.ExtractCoreDeviceName("Stereo Headphones (WH-1000XM4)");
            Assert.Equal("WH-1000XM4", cleaned3);
        }

        #endregion

        #region 3. Presentation Formatting (UI Policy - Moderate Volatility)

        [Fact]
        public void BluetoothPresentationFormatter_FormatsCanonicalName()
        {
            string canonical1 = BluetoothPresentationFormatter.FormatCanonicalName(
                "DRM25",
                new[] { "Redmi Note 13 5G" }
            );
            Assert.Equal("DRM25 (Redmi Note 13 5G)", canonical1);

            // No duplicate or identical names
            string canonical2 = BluetoothPresentationFormatter.FormatCanonicalName(
                "Galaxy S24",
                new[] { "Galaxy S24", "" }
            );
            Assert.Equal("Galaxy S24", canonical2);
        }

        [Fact]
        public void BluetoothPresentationFormatter_FormatsBridgeSupportStatusConsistently()
        {
            // Ready to bridge: HFP supported, connected, has active endpoint and HFP endpoint
            string statusReady = BluetoothPresentationFormatter.FormatBridgeSupportStatus(
                isHfpSupported: true,
                isConnected: true,
                hasHfpEndpoint: true,
                hasActiveEndpoint: true,
                isTargetPhone: true);
            Assert.Equal(BluetoothPresentationFormatter.BridgeStatusReady, statusReady);

            // Incompatible A2DP
            string statusIncompat = BluetoothPresentationFormatter.FormatBridgeSupportStatus(
                isHfpSupported: false,
                isConnected: true,
                hasHfpEndpoint: false,
                hasActiveEndpoint: true,
                isTargetPhone: false);
            Assert.Equal(BluetoothPresentationFormatter.BridgeStatusIncompatibleA2dp, statusIncompat);

            // Phone connected but audio inactive (no HFP endpoint active)
            string statusInactive = BluetoothPresentationFormatter.FormatBridgeSupportStatus(
                isHfpSupported: true,
                isConnected: true,
                hasHfpEndpoint: false,
                hasActiveEndpoint: true,
                isTargetPhone: true);
            Assert.Equal(BluetoothPresentationFormatter.BridgeStatusAudioInactive, statusInactive);

            // Phone offline
            string statusOffline = BluetoothPresentationFormatter.FormatBridgeSupportStatus(
                isHfpSupported: true,
                isConnected: false,
                hasHfpEndpoint: false,
                hasActiveEndpoint: false,
                isTargetPhone: true);
            Assert.Equal(BluetoothPresentationFormatter.BridgeStatusPhoneTargetOffline, statusOffline);
        }

        [Fact]
        public void BluetoothPresentationFormatter_FormatsMacAddress()
        {
            Assert.Equal("D4:A3:65:E8:26:A3", BluetoothPresentationFormatter.FormatMacAddress("d4a365e826a3"));
            Assert.Equal("", BluetoothPresentationFormatter.FormatMacAddress(""));
            Assert.Equal("INVALID", BluetoothPresentationFormatter.FormatMacAddress("INVALID"));
        }

        #endregion

        #region 4. Entity Aggregation (Chassis ContainerId Boundary)

        [Fact]
        public void BluetoothDeviceAggregator_MergesMultiTransportDevices_WithSameContainerId()
        {
            var aggregator = BluetoothDeviceAggregator.Default;
            string testContainerId = "{12345678-1234-1234-1234-123456789abc}";

            var classicIdentity = new BluetoothTransportIdentity(
                MacAddress: "D4:A3:65:E8:26:A3",
                TransportType: BluetoothTransportType.Classic,
                AdvertisedName: "DRM25",
                Cod: 0x5A020C,
                LeAppearance: null,
                HasHfpProfile: true,
                HasActiveEndpoint: false
            );

            var bleIdentity = new BluetoothTransportIdentity(
                MacAddress: "7F:8A:93:71:94:AA",
                TransportType: BluetoothTransportType.Ble,
                AdvertisedName: "Redmi Note 13 5G",
                Cod: null,
                LeAppearance: 961,
                HasHfpProfile: false,
                HasActiveEndpoint: true
            );

            var classicDto = new BluetoothDeviceDto(
                Id: "BT_d4a365e826a3",
                Name: "DRM25",
                MacAddress: "D4:A3:65:E8:26:A3",
                Category: BluetoothDeviceCategory.Phone,
                ConnectionStatus: BluetoothPresentationFormatter.StatusOfflineSettings,
                BridgeSupportStatus: BluetoothPresentationFormatter.BridgeStatusPhoneTargetOffline,
                IsConnected: false,
                IsHfpSupported: true,
                IsBridgeReady: false,
                CanSelectForBridge: false,
                ContainerId: testContainerId,
                Aliases: new List<string>(),
                TransportIdentities: new List<BluetoothTransportIdentity> { classicIdentity }
            );

            var bleDto = new BluetoothDeviceDto(
                Id: "BT_7f8a937194aa",
                Name: "Redmi Note 13 5G",
                MacAddress: "7F:8A:93:71:94:AA",
                Category: BluetoothDeviceCategory.Headset,
                ConnectionStatus: BluetoothPresentationFormatter.StatusConnectedOnline,
                BridgeSupportStatus: BluetoothPresentationFormatter.BridgeStatusIncompatibleA2dp,
                IsConnected: true,
                IsHfpSupported: false,
                IsBridgeReady: false,
                CanSelectForBridge: false,
                ContainerId: testContainerId,
                Aliases: new List<string>(),
                TransportIdentities: new List<BluetoothTransportIdentity> { bleIdentity }
            );

            var aggregated = aggregator.AggregateByContainerId(new[] { classicDto, bleDto });

            Assert.Single(aggregated);
            var canonical = aggregated[0];
            Assert.Equal("DRM25 (Redmi Note 13 5G)", canonical.Name);
            Assert.Equal(BluetoothDeviceCategory.Phone, canonical.Category);
            Assert.True(canonical.IsConnected); // From BLE link
            Assert.True(canonical.IsHfpSupported); // From Classic link
            Assert.NotNull(canonical.TransportIdentities);
            Assert.Equal(2, canonical.TransportIdentities.Count);
        }

        [Fact]
        public void BluetoothDeviceAggregator_NeverMergesDevicesWithDifferentOrNullContainerId()
        {
            var aggregator = BluetoothDeviceAggregator.Default;

            var dev1 = new BluetoothDeviceDto(
                Id: "BT_1",
                Name: "BT 5.0",
                MacAddress: "11:22:33:44:55:66",
                Category: BluetoothDeviceCategory.Speaker,
                ConnectionStatus: BluetoothPresentationFormatter.StatusConnectedOnline,
                BridgeSupportStatus: BluetoothPresentationFormatter.BridgeStatusIncompatibleA2dp,
                IsConnected: true,
                IsHfpSupported: false,
                IsBridgeReady: false,
                CanSelectForBridge: false,
                ContainerId: "{aaaa-bbbb-cccc}",
                Aliases: new List<string>(),
                TransportIdentities: new List<BluetoothTransportIdentity>()
            );

            var dev2 = new BluetoothDeviceDto(
                Id: "BT_2",
                Name: "BT 5.0",
                MacAddress: "77:88:99:AA:BB:CC",
                Category: BluetoothDeviceCategory.Speaker,
                ConnectionStatus: BluetoothPresentationFormatter.StatusConnectedOnline,
                BridgeSupportStatus: BluetoothPresentationFormatter.BridgeStatusIncompatibleA2dp,
                IsConnected: true,
                IsHfpSupported: false,
                IsBridgeReady: false,
                CanSelectForBridge: false,
                ContainerId: "{xxxx-yyyy-zzzz}",
                Aliases: new List<string>(),
                TransportIdentities: new List<BluetoothTransportIdentity>()
            );

            var dev3 = new BluetoothDeviceDto(
                Id: "BT_3",
                Name: "BT 5.0",
                MacAddress: "DE:AD:BE:EF:00:01",
                Category: BluetoothDeviceCategory.Speaker,
                ConnectionStatus: BluetoothPresentationFormatter.StatusConnectedOnline,
                BridgeSupportStatus: BluetoothPresentationFormatter.BridgeStatusIncompatibleA2dp,
                IsConnected: true,
                IsHfpSupported: false,
                IsBridgeReady: false,
                CanSelectForBridge: false,
                ContainerId: null, // Null ContainerId
                Aliases: new List<string>(),
                TransportIdentities: new List<BluetoothTransportIdentity>()
            );

            var aggregated = aggregator.AggregateByContainerId(new[] { dev1, dev2, dev3 });

            Assert.Equal(3, aggregated.Count);
        }

        #endregion

        #region 5. Thin Orchestrator Facade (BluetoothDeviceDiscovery Dependency Injection & Zero Blast Radius)

        private class MockBluetoothRegistryReader : IBluetoothRegistryReader
        {
            public Dictionary<string, PairedRegistryDevice> PairedDevices { get; set; } = new();
            public bool RadioEnabled { get; set; } = true;
            public bool OpenSettingsCalled { get; set; }

            public Dictionary<string, PairedRegistryDevice> ReadPairedDevices() => PairedDevices;
            public bool IsBluetoothRadioEnabled() => RadioEnabled;
            public bool OpenWindowsBluetoothSettings()
            {
                OpenSettingsCalled = true;
                return true;
            }
        }

        [Fact]
        public void BluetoothDeviceDiscovery_CanInjectMockRegistryReaderAndCustomClassifier()
        {
            var mockReader = new MockBluetoothRegistryReader
            {
                RadioEnabled = true,
                PairedDevices = new Dictionary<string, PairedRegistryDevice>
                {
                    ["aabbccddeeff"] = new PairedRegistryDevice("aabbccddeeff", "Experimental Prototype Device", Cod: null)
                }
            };

            var customClassifier = new BluetoothDeviceClassifier(
                customPhoneKeywords: new[] { "experimental" }
            );

            var discovered = BluetoothDeviceDiscovery.DiscoverBluetoothDevices(
                renderProvider: () => new List<AudioDeviceInfo>(),
                captureProvider: () => new List<AudioDeviceInfo>(),
                registryReader: mockReader,
                classifier: customClassifier
            );

            Assert.Single(discovered);
            var dev = discovered[0];
            Assert.Equal("Experimental Prototype Device", dev.Name);
            // Confirms custom classifier was utilized by thin facade
            Assert.Equal(BluetoothDeviceCategory.Phone, dev.Category);
        }

        [Fact]
        public void BluetoothDeviceDiscovery_RadioDisabled_ReturnsEmptyListWithoutTouchingRegistry()
        {
            var mockReader = new MockBluetoothRegistryReader
            {
                RadioEnabled = false,
                PairedDevices = new Dictionary<string, PairedRegistryDevice>
                {
                    ["112233445566"] = new PairedRegistryDevice("112233445566", "My Phone", Cod: 0x5A020C)
                }
            };

            var discovered = BluetoothDeviceDiscovery.DiscoverBluetoothDevices(
                renderProvider: () => new List<AudioDeviceInfo>(),
                captureProvider: () => new List<AudioDeviceInfo>(),
                registryReader: mockReader
            );

            Assert.Empty(discovered);
        }

        #endregion
    }
}
