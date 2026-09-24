using System;
using System.Collections.Generic;
using System.Linq;
using NAudio.CoreAudioApi;
using PhoneMicBridge.Application;

namespace PhoneMicBridge.Devices
{
    /// <summary>
    /// OS-level Bluetooth device discovery Orchestrator (Thin Facade).
    /// Coordinates between:
    /// - Low-Level OS Registry Access Mechanism (BluetoothRegistryReader)
    /// - Domain Standard Bitfield Specification (BluetoothClassOfDevice)
    /// - Heuristic Classification Policy (BluetoothDeviceClassifier)
    /// - Chassis Entity Aggregation Policy (BluetoothDeviceAggregator)
    /// - Presentation Formatting Policy (BluetoothPresentationFormatter)
    ///
    /// Preserves 100% backward-compatible API contracts for consumers and tests.
    /// </summary>
    public static class BluetoothDeviceDiscovery
    {
        public const string BthRegistryPath = BluetoothRegistryReader.BthRegistryPath;

        /// <summary>
        /// Discovers all paired Bluetooth devices from Windows Registry, reconciles with Audio Endpoints,
        /// and applies 5 Verification Gates using 3-Tier Evidence-Based Architecture.
        /// </summary>
        public static List<BluetoothDeviceDto> DiscoverBluetoothDevices(
            Func<List<AudioDeviceInfo>>? renderProvider = null,
            Func<List<AudioDeviceInfo>>? captureProvider = null,
            Func<bool>? radioStateProvider = null,
            Func<Dictionary<string, PairedRegistryDevice>>? pairedRegistryProvider = null,
            Func<Dictionary<string, string>>? legacyRegistryProvider = null,
            IBluetoothRegistryReader? registryReader = null,
            IBluetoothDeviceClassifier? classifier = null,
            IBluetoothDeviceAggregator? aggregator = null)
        {
            var activeReader = registryReader ?? BluetoothRegistryReader.Instance;
            var activeClassifier = classifier ?? BluetoothDeviceClassifier.Default;
            var activeAggregator = aggregator ?? BluetoothDeviceAggregator.Default;

            var isRadioEnabled = IsBluetoothRadioEnabled(radioStateProvider, renderProvider, captureProvider, activeReader);
            if (!isRadioEnabled)
            {
                return new List<BluetoothDeviceDto>();
            }

            var renders = renderProvider?.Invoke() ?? DeviceDiscovery.GetRenderDevices();
            var captures = captureProvider?.Invoke() ?? DeviceDiscovery.GetCaptureDevices();

            Dictionary<string, PairedRegistryDevice> pairedFromRegistry;
            if (pairedRegistryProvider != null)
            {
                pairedFromRegistry = pairedRegistryProvider();
            }
            else if (legacyRegistryProvider != null)
            {
                pairedFromRegistry = legacyRegistryProvider()
                    .ToDictionary(kvp => kvp.Key, kvp => new PairedRegistryDevice(kvp.Key, kvp.Value), StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                pairedFromRegistry = activeReader.ReadPairedDevices();
            }

            var resultDict = new Dictionary<string, BluetoothDeviceDto>(StringComparer.OrdinalIgnoreCase);

            // Step 1: Ingest all paired devices from Windows Registry (Gate 1 Authority)
            foreach (var kvp in pairedFromRegistry)
            {
                string mac = kvp.Key;
                var regDev = kvp.Value;
                string rawName = regDev.Name;
                if (string.IsNullOrWhiteSpace(rawName) || rawName.Equals("Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var matchedEndpoints = FindMatchingAudioEndpoints(rawName, mac, renders, captures);

                bool hasActiveEndpoint = matchedEndpoints.Any(e => e.State == DeviceState.Active);
                bool hasHfpEndpoint = matchedEndpoints.Any(e => e.IsTargetHfp ||
                                                                e.Name.Contains("hands-free", StringComparison.OrdinalIgnoreCase) ||
                                                                e.Name.Contains("handsfree", StringComparison.OrdinalIgnoreCase) ||
                                                                e.Name.Contains("hf audio", StringComparison.OrdinalIgnoreCase));

                // 3-Tier Evidence-Based Classification for Gate 4
                var category = activeClassifier.ClassifyDeviceArchetype(regDev, matchedEndpoints, rawName);

                bool isTargetPhone = category == BluetoothDeviceCategory.Phone;
                bool isSpeaker = category == BluetoothDeviceCategory.Speaker;

                // Gate 2: Physical Link State
                bool isConnected = hasActiveEndpoint;
                string connStatus = BluetoothPresentationFormatter.FormatConnectionStatus(isConnected);

                // Gate 3: HFP Profile Support (Explicit Evidence: Speakers cannot bridge HFP; Phones and Headsets with HFP can)
                bool isHfpSupported;
                if (isSpeaker)
                {
                    isHfpSupported = false;
                }
                else if (isTargetPhone)
                {
                    // Phones inherently support telephony Audio Gateway profile
                    isHfpSupported = true;
                }
                else
                {
                    // For Headsets / Others: HFP is supported if indicated by CoD Headset or registered HFP endpoints
                    bool codIndicatesHeadset = regDev.Cod.HasValue && new BluetoothClassOfDevice(regDev.Cod.Value).IsConfirmedHeadset;
                    isHfpSupported = hasHfpEndpoint || codIndicatesHeadset;
                }

                // Gate 4: Bridge Target Recon
                string bridgeStatus = BluetoothPresentationFormatter.FormatBridgeSupportStatus(
                    isHfpSupported,
                    isConnected,
                    hasHfpEndpoint,
                    hasActiveEndpoint,
                    isTargetPhone);

                bool isBridgeReady = isHfpSupported && isConnected && hasHfpEndpoint && hasActiveEndpoint;

                // Gate 5: Selection Guard
                bool canSelect = isBridgeReady;

                string id = matchedEndpoints.FirstOrDefault(e => e.IsTargetHfp)?.Id
                         ?? matchedEndpoints.FirstOrDefault()?.Id
                         ?? $"BT_{mac}";

                var transport = regDev.Cod.HasValue
                    ? BluetoothTransportType.Classic
                    : (regDev.LeAppearance.HasValue || regDev.BoundSubKeys?.Any(k => k.Contains("LE", StringComparison.OrdinalIgnoreCase)) == true
                        ? BluetoothTransportType.Ble
                        : BluetoothTransportType.Classic);

                var transportIdentity = new BluetoothTransportIdentity(
                    MacAddress: BluetoothPresentationFormatter.FormatMacAddress(mac),
                    TransportType: transport,
                    AdvertisedName: rawName,
                    Cod: regDev.Cod,
                    LeAppearance: regDev.LeAppearance,
                    HasHfpProfile: isHfpSupported,
                    HasActiveEndpoint: isConnected
                );

                resultDict[mac] = new BluetoothDeviceDto(
                    Id: id,
                    Name: rawName,
                    MacAddress: BluetoothPresentationFormatter.FormatMacAddress(mac),
                    Category: category,
                    ConnectionStatus: connStatus,
                    BridgeSupportStatus: bridgeStatus,
                    IsConnected: isConnected,
                    IsHfpSupported: isHfpSupported,
                    IsBridgeReady: isBridgeReady,
                    CanSelectForBridge: canSelect,
                    ContainerId: regDev.ContainerId,
                    Aliases: new List<string>(),
                    TransportIdentities: new List<BluetoothTransportIdentity> { transportIdentity }
                );
            }

            // Step 2: Fallback for audio endpoints not in registry
            var claimedEndpointIds = new HashSet<string>(resultDict.Values.Select(v => v.Id), StringComparer.OrdinalIgnoreCase);

            foreach (var d in renders.Concat(captures))
            {
                if (claimedEndpointIds.Contains(d.Id))
                {
                    continue;
                }

                if (!d.Name.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) &&
                    !d.Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) &&
                    !d.IsTargetHfp)
                {
                    continue;
                }

                string normalizedName = activeClassifier.ExtractCoreDeviceName(d.Name);
                if (resultDict.Values.Any(v => v.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var cat = activeClassifier.DetermineCategoryHeuristic(normalizedName);
                bool isActive = d.State == DeviceState.Active;
                bool isHfp = d.IsTargetHfp || d.Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase);

                string bridgeStatus = isHfp
                    ? (isActive ? BluetoothPresentationFormatter.BridgeStatusReady : BluetoothPresentationFormatter.BridgeStatusHfpOffline)
                    : BluetoothPresentationFormatter.BridgeStatusIncompatibleA2dp;

                bool isConnected = isActive;
                bool isBridgeReady = isActive && isHfp;

                resultDict[d.Id] = new BluetoothDeviceDto(
                    Id: d.Id,
                    Name: normalizedName,
                    MacAddress: "",
                    Category: cat,
                    ConnectionStatus: BluetoothPresentationFormatter.FormatConnectionStatus(isConnected),
                    BridgeSupportStatus: bridgeStatus,
                    IsConnected: isConnected,
                    IsHfpSupported: isHfp,
                    IsBridgeReady: isBridgeReady,
                    CanSelectForBridge: isBridgeReady,
                    ContainerId: null,
                    Aliases: new List<string>(),
                    TransportIdentities: new List<BluetoothTransportIdentity>()
                );
            }

            // Step 3: Canonical Physical Device Aggregation (ContainerId Boundary)
            var canonicalDevices = activeAggregator.AggregateByContainerId(resultDict.Values);

            // Order: Ready devices first, then Phones, then Headsets, then Speakers, then alphabetical
            return canonicalDevices
                .OrderByDescending(d => d.IsBridgeReady)
                .ThenBy(d => d.Category switch
                {
                    BluetoothDeviceCategory.Phone => 1,
                    BluetoothDeviceCategory.Headset => 2,
                    BluetoothDeviceCategory.Speaker => 3,
                    _ => 4
                })
                .ThenBy(d => d.Name)
                .ToList();
        }

        /// <summary>
        /// Backward-compatible overload for tests and callers passing legacy Dictionary&lt;string, string&gt;.
        /// </summary>
        public static List<BluetoothDeviceDto> DiscoverBluetoothDevices(
            Func<List<AudioDeviceInfo>>? renderProvider,
            Func<List<AudioDeviceInfo>>? captureProvider,
            Func<bool>? radioStateProvider,
            Func<Dictionary<string, string>>? pairedRegistryProvider)
        {
            return DiscoverBluetoothDevices(
                renderProvider,
                captureProvider,
                radioStateProvider,
                pairedRegistryProvider: null,
                legacyRegistryProvider: pairedRegistryProvider
            );
        }

        /// <summary>
        /// Classifies a device archetype using the default 3-tier classification policy.
        /// </summary>
        public static BluetoothDeviceCategory ClassifyDeviceArchetype(
            PairedRegistryDevice regDev,
            List<AudioDeviceInfo> matchedEndpoints,
            string rawName)
        {
            return BluetoothDeviceClassifier.Default.ClassifyDeviceArchetype(regDev, matchedEndpoints, rawName);
        }

        /// <summary>
        /// Checks if Bluetooth Radio is enabled via provider or low-level registry mechanism.
        /// </summary>
        public static bool IsBluetoothRadioEnabled(
            Func<bool>? radioStateProvider = null,
            Func<List<AudioDeviceInfo>>? renderProvider = null,
            Func<List<AudioDeviceInfo>>? captureProvider = null,
            IBluetoothRegistryReader? registryReader = null)
        {
            if (radioStateProvider != null)
            {
                return radioStateProvider();
            }

            var reader = registryReader ?? BluetoothRegistryReader.Instance;
            return reader.IsBluetoothRadioEnabled();
        }

        /// <summary>
        /// Opens native Windows Bluetooth Settings via shell execution mechanism.
        /// </summary>
        public static bool OpenWindowsBluetoothSettings(IBluetoothRegistryReader? registryReader = null)
        {
            var reader = registryReader ?? BluetoothRegistryReader.Instance;
            return reader.OpenWindowsBluetoothSettings();
        }

        /// <summary>
        /// Reads paired devices directly from the Windows Registry mechanism.
        /// </summary>
        public static Dictionary<string, PairedRegistryDevice> ReadPairedDevicesFromRegistry(IBluetoothRegistryReader? registryReader = null)
        {
            var reader = registryReader ?? BluetoothRegistryReader.Instance;
            return reader.ReadPairedDevices();
        }

        private static List<AudioDeviceInfo> FindMatchingAudioEndpoints(
            string deviceName,
            string macAddress,
            List<AudioDeviceInfo> renders,
            List<AudioDeviceInfo> captures)
        {
            var all = renders.Concat(captures);
            var cleanDevName = deviceName.ToLowerInvariant();

            return all.Where(e =>
            {
                var lower = e.Name.ToLowerInvariant();
                return lower.Contains(cleanDevName) ||
                       (!string.IsNullOrWhiteSpace(macAddress) && e.Id.Contains(macAddress, StringComparison.OrdinalIgnoreCase)) ||
                       (cleanDevName.Contains("redmi") && e.IsTargetHfp);
            }).ToList();
        }
    }
}
