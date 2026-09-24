using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
using PhoneMicBridge.Application;

namespace PhoneMicBridge.Devices
{
    /// <summary>
    /// Bluetooth SIG Assigned Numbers representation of Class of Device (CoD).
    /// Enforces strict separation between Service Class capability flags and Device Type (Major + Minor).
    /// </summary>
    public readonly record struct BluetoothClassOfDevice(int RawValue)
    {
        // 24-bit CoD Layout: [23..13 Service Classes (11b)] [12..8 Major Device (5b)] [7..2 Minor Device (6b)] [1..0 Format (2b)]
        public int FormatType => RawValue & 0x03; // Bits 0-1 (Always 00b)
        public int MinorDeviceClass => (RawValue >> 2) & 0x3F; // Bits 2-7
        public int MajorDeviceClass => (RawValue >> 8) & 0x1F; // Bits 8-12
        public int MajorServiceClasses => (RawValue >> 13) & 0x7FF; // Bits 13-23

        // Bluetooth SIG Major Device Classes (Bits 8-12)
        public const int MajorMiscellaneous = 0x00;
        public const int MajorComputer      = 0x01;
        public const int MajorPhone         = 0x02;
        public const int MajorNetworkAp     = 0x03;
        public const int MajorAudioVideo    = 0x04;
        public const int MajorPeripheral    = 0x05;
        public const int MajorImaging       = 0x06;
        public const int MajorWearable      = 0x07;

        // Bluetooth SIG Minor Device Classes for Audio/Video (Major = 0x04)
        public const int MinorAudioWearableHeadset = 0x01; // 000001b (Wearable Headset Device with Mic)
        public const int MinorAudioHandsFree      = 0x02; // 000010b (Hands-free unit)
        public const int MinorAudioMicrophone     = 0x04; // 000100b (Standalone Microphone)
        public const int MinorAudioLoudspeaker    = 0x05; // 000101b (Loudspeaker)
        public const int MinorAudioHeadphones     = 0x06; // 000110b (Headphones / Stereo Playback)
        public const int MinorAudioPortable       = 0x07; // 000111b (Portable Audio)

        // Bluetooth SIG Minor Device Classes for Phone (Major = 0x02)
        public const int MinorPhoneCellular   = 0x01;
        public const int MinorPhoneCordless   = 0x02;
        public const int MinorPhoneSmartphone = 0x03;
        public const int MinorPhoneModem      = 0x04;

        // Classification Rules based strictly on (Major, Minor) pairs - NOT service bits alone!
        public bool IsConfirmedLoudspeaker =>
            MajorDeviceClass == MajorAudioVideo &&
            (MinorDeviceClass == MinorAudioLoudspeaker || MinorDeviceClass == MinorAudioHeadphones || MinorDeviceClass == MinorAudioPortable);

        public bool IsConfirmedHeadset =>
            MajorDeviceClass == MajorAudioVideo &&
            (MinorDeviceClass == MinorAudioWearableHeadset || MinorDeviceClass == MinorAudioHandsFree);

        public bool IsConfirmedPhone =>
            MajorDeviceClass == MajorPhone &&
            (MinorDeviceClass == MinorPhoneCellular || MinorDeviceClass == MinorPhoneSmartphone || MinorDeviceClass == MinorPhoneCordless);

        // Major Service Class Bitfield Queries (Contextual Capabilities only)
        public bool HasTelephonyService => (MajorServiceClasses & (1 << (22 - 13))) != 0; // Bit 22
        public bool HasAudioService     => (MajorServiceClasses & (1 << (21 - 13))) != 0; // Bit 21
        public bool HasCapturingService => (MajorServiceClasses & (1 << (19 - 13))) != 0; // Bit 19
        public bool HasRenderingService => (MajorServiceClasses & (1 << (18 - 13))) != 0; // Bit 18
    }

    /// <summary>
    /// Explicit paired device metadata harvested from Windows BTHPORT Registry.
    /// </summary>
    public record PairedRegistryDevice(
        string Mac,
        string Name,
        int? Cod = null,
        int? LeAppearance = null,
        IReadOnlyList<string>? BoundSubKeys = null,
        string? ContainerId = null
    );

    /// <summary>
    /// OS-level Bluetooth device discovery adhering to the 5 Verification Gates:
    /// Gate 1 (Paired Check via Windows Registry) -> Gate 2 (Physical Link State: Online vs Offline)
    /// -> Gate 3 (HFP Profile Support) -> Gate 4 (Bridge Target Recon) -> Gate 5 (Selection Guard).
    /// </summary>
    public static class BluetoothDeviceDiscovery
    {
        private static readonly string[] PhoneKeywords =
        {
            "redmi", "galaxy", "iphone", "pixel", "xiaomi", "oppo", "vivo", "joy", "phone", "huawei", "realme", "oneplus", "samsung", "motorola", "sony xperia"
        };

        private static readonly string[] HeadsetKeywords =
        {
            "headphone", "headset", "buds", "airpod", "tws", "sy-t25", "m18", "m28", "m38", "earphone", "realfit"
        };

        private static readonly string[] SpeakerKeywords =
        {
            "speaker", "music", "soundbar", "bt-speaker"
        };

        public const string BthRegistryPath = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";

        /// <summary>
        /// Discovers all paired Bluetooth devices from Windows Registry, reconciles with Audio Endpoints,
        /// and applies 5 Verification Gates using 3-Tier Evidence-Based Architecture.
        /// </summary>
        public static List<BluetoothDeviceDto> DiscoverBluetoothDevices(
            Func<List<AudioDeviceInfo>>? renderProvider = null,
            Func<List<AudioDeviceInfo>>? captureProvider = null,
            Func<bool>? radioStateProvider = null,
            Func<Dictionary<string, PairedRegistryDevice>>? pairedRegistryProvider = null,
            Func<Dictionary<string, string>>? legacyRegistryProvider = null)
        {
            var isRadioEnabled = IsBluetoothRadioEnabled(radioStateProvider, renderProvider, captureProvider);
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
                pairedFromRegistry = ReadPairedDevicesFromRegistry();
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
                var category = ClassifyDeviceArchetype(regDev, matchedEndpoints, rawName);

                bool isTargetPhone = category == BluetoothDeviceCategory.Phone;
                bool isSpeaker = category == BluetoothDeviceCategory.Speaker;

                // Gate 2: Physical Link State
                bool isConnected = hasActiveEndpoint;
                string connStatus = isConnected ? "Connected (Online)" : "Offline (Connect in Settings)";

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
                string bridgeStatus;
                bool isBridgeReady = false;

                if (!isHfpSupported)
                {
                    bridgeStatus = "Incompatible (A2DP Only)";
                }
                else
                {
                    if (isConnected && hasHfpEndpoint && hasActiveEndpoint)
                    {
                        bridgeStatus = "READY TO BRIDGE";
                        isBridgeReady = true;
                    }
                    else if (isTargetPhone)
                    {
                        bridgeStatus = isConnected
                            ? "Audio Inactive (Enable in Settings)"
                            : "HFP Phone Target (Offline)";
                    }
                    else
                    {
                        bridgeStatus = isConnected
                            ? "HFP Connected"
                            : "HFP Supported (Offline)";
                    }
                }

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
                    MacAddress: FormatMac(mac),
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
                    MacAddress: FormatMac(mac),
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

                string normalizedName = ExtractCoreDeviceName(d.Name);
                if (resultDict.Values.Any(v => v.Name.Equals(normalizedName, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var cat = DetermineCategoryHeuristic(normalizedName);
                bool isActive = d.State == DeviceState.Active;
                bool isHfp = d.IsTargetHfp || d.Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase);

                string bridgeStatus = isHfp
                    ? (isActive ? "READY TO BRIDGE" : "HFP Supported (Offline)")
                    : "Incompatible (A2DP Only)";

                bool isConnected = isActive;
                bool isBridgeReady = isActive && isHfp;

                resultDict[d.Id] = new BluetoothDeviceDto(
                    Id: d.Id,
                    Name: normalizedName,
                    MacAddress: "",
                    Category: cat,
                    ConnectionStatus: isConnected ? "Connected (Online)" : "Offline (Connect in Settings)",
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
            // Rule 5 & 6: Devices with different ContainerId or null ContainerId are NEVER merged.
            // Only aggregate when an identical non-empty ContainerId is shared across transport identities.
            var canonicalDevices = new List<BluetoothDeviceDto>();
            var groupedByContainer = resultDict.Values
                .Where(d => !string.IsNullOrWhiteSpace(d.ContainerId))
                .GroupBy(d => d.ContainerId!, StringComparer.OrdinalIgnoreCase);

            var processedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var group in groupedByContainer)
            {
                var items = group.ToList();
                if (items.Count == 1)
                {
                    canonicalDevices.Add(items[0]);
                    processedIds.Add(items[0].Id);
                }
                else
                {
                    // Multi-transport aggregation (e.g. Classic BR/EDR + BLE)
                    // Rule 2: Classic/BLE identities preserved inside canonical device
                    // Rule 3: HFP capability from Classic is preserved
                    // Rule 4: BLE metadata is preserved
                    var classicItem = items.FirstOrDefault(i => i.TransportIdentities?.Any(t => t.TransportType == BluetoothTransportType.Classic) == true)
                                   ?? items.FirstOrDefault(i => i.IsHfpSupported);
                    var primary = classicItem ?? items[0];

                    var allTransports = items.SelectMany(i => i.TransportIdentities ?? Enumerable.Empty<BluetoothTransportIdentity>()).ToList();
                    var names = items.Select(i => i.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

                    // Canonical name: Primary name + alternate model name, e.g. "DRM25 (Redmi Note 13 5G)"
                    var aliasList = names.Where(n => !n.Equals(primary.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                    string canonicalName = aliasList.Count > 0
                        ? $"{primary.Name} ({string.Join(", ", aliasList)})"
                        : primary.Name;

                    bool anyHfp = items.Any(i => i.IsHfpSupported);
                    bool anyConnected = items.Any(i => i.IsConnected);
                    bool anyBridgeReady = items.Any(i => i.IsBridgeReady);
                    bool anyCanSelect = items.Any(i => i.CanSelectForBridge);
                    var category = items.Any(i => i.Category == BluetoothDeviceCategory.Phone)
                        ? BluetoothDeviceCategory.Phone
                        : primary.Category;

                    string bridgeStatus = primary.BridgeSupportStatus;
                    if (anyBridgeReady)
                    {
                        bridgeStatus = "READY TO BRIDGE";
                    }
                    else if (category == BluetoothDeviceCategory.Phone)
                    {
                        bridgeStatus = anyConnected ? "Audio Inactive (Enable in Settings)" : "HFP Phone Target (Offline)";
                    }

                    canonicalDevices.Add(primary with
                    {
                        Name = canonicalName,
                        Category = category,
                        IsConnected = anyConnected,
                        IsHfpSupported = anyHfp,
                        IsBridgeReady = anyBridgeReady,
                        CanSelectForBridge = anyCanSelect,
                        BridgeSupportStatus = bridgeStatus,
                        Aliases = aliasList,
                        TransportIdentities = allTransports
                    });

                    foreach (var it in items)
                    {
                        processedIds.Add(it.Id);
                    }
                }
            }

            // Include devices without ContainerId (guaranteed not merged)
            foreach (var dev in resultDict.Values)
            {
                if (!processedIds.Contains(dev.Id))
                {
                    canonicalDevices.Add(dev);
                }
            }

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
        /// 3-Tier Classification Engine:
        /// Tier 1: Bluetooth SIG CoD (Major + Minor) explicit metadata.
        /// Tier 2: OS CoreAudio PnP Endpoint representations (Hands-Free HF Audio).
        /// Tier 3: Heuristic Tokenized Naming Fallback.
        /// </summary>
        public static BluetoothDeviceCategory ClassifyDeviceArchetype(
            PairedRegistryDevice regDev,
            List<AudioDeviceInfo> matchedEndpoints,
            string rawName)
        {
            // Tier 1: Classic Bluetooth CoD (Major + Minor Device Class)
            if (regDev.Cod.HasValue && regDev.Cod.Value != 0)
            {
                var cod = new BluetoothClassOfDevice(regDev.Cod.Value);
                if (cod.IsConfirmedPhone) return BluetoothDeviceCategory.Phone;
                if (cod.IsConfirmedLoudspeaker) return BluetoothDeviceCategory.Speaker;
                if (cod.IsConfirmedHeadset) return BluetoothDeviceCategory.Headset;
            }

            // Tier 2: OS Audio Endpoint Representation & BLE GAP Appearance
            bool hasHandsFreePnp = matchedEndpoints.Any(e =>
                e.IsTargetHfp ||
                e.Name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) ||
                e.Name.Contains("HF Audio", StringComparison.OrdinalIgnoreCase));

            if (hasHandsFreePnp)
            {
                // If device has registered Hands-Free endpoint, check if naming or archetype indicates a phone
                if (PhoneKeywords.Any(k => rawName.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    return BluetoothDeviceCategory.Phone;
                }
                return BluetoothDeviceCategory.Headset;
            }

            // BLE GAP Appearance (e.g. 961 = 0x03C1 -> Category 15 = Generic Media Player)
            if (regDev.LeAppearance.HasValue && regDev.LeAppearance.Value != 0)
            {
                int categoryCode = (regDev.LeAppearance.Value >> 6) & 0x3FF;
                if (categoryCode == 15) // Generic Media Player (960)
                {
                    return BluetoothDeviceCategory.Headset;
                }
            }

            // Tier 3: Heuristic Fallback (Token disambiguation)
            return DetermineCategoryHeuristic(rawName);
        }

        private static BluetoothDeviceCategory DetermineCategoryHeuristic(string name)
        {
            var lower = name.ToLowerInvariant();
            if (HeadsetKeywords.Any(k => lower.Contains(k))) return BluetoothDeviceCategory.Headset;
            if (SpeakerKeywords.Any(k => lower.Contains(k))) return BluetoothDeviceCategory.Speaker;
            if (PhoneKeywords.Any(k => lower.Contains(k))) return BluetoothDeviceCategory.Phone;
            return BluetoothDeviceCategory.Other;
        }

        public static bool IsBluetoothRadioEnabled(
            Func<bool>? radioStateProvider = null,
            Func<List<AudioDeviceInfo>>? renderProvider = null,
            Func<List<AudioDeviceInfo>>? captureProvider = null)
        {
            if (radioStateProvider != null)
            {
                return radioStateProvider();
            }

            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
                using var key = baseKey.OpenSubKey(BthRegistryPath);
                return key != null;
            }
            catch
            {
                return false;
            }
        }

        public static bool OpenWindowsBluetoothSettings()
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "ms-settings:bluetooth",
                    UseShellExecute = true
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static Dictionary<string, PairedRegistryDevice> ReadPairedDevicesFromRegistry()
        {
            var results = new Dictionary<string, PairedRegistryDevice>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
                using var devKey = baseKey.OpenSubKey(BthRegistryPath);
                if (devKey == null) return results;

                foreach (var subKeyName in devKey.GetSubKeyNames())
                {
                    using var sub = devKey.OpenSubKey(subKeyName);
                    if (sub == null) continue;

                    string name = "";
                    if (sub.GetValue("Name") is byte[] nameBytes && nameBytes.Length > 0)
                    {
                        name = Encoding.UTF8.GetString(nameBytes).TrimEnd('\0', ' ');
                    }
                    else if (sub.GetValue("LEName") is byte[] leBytes && leBytes.Length > 0)
                    {
                        name = Encoding.UTF8.GetString(leBytes).TrimEnd('\0', ' ');
                    }

                    int? cod = null;
                    if (sub.GetValue("COD") is int codInt)
                    {
                        cod = codInt;
                    }
                    else if (sub.GetValue("COD") is long codLong)
                    {
                        cod = (int)codLong;
                    }

                    int? leAppearance = null;
                    if (sub.GetValue("LEAppearance") is int appInt)
                    {
                        leAppearance = appInt;
                    }

                    var subKeys = sub.GetSubKeyNames();
                    string? containerId = TryResolveContainerId(baseKey, subKeyName, sub);

                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        results[subKeyName] = new PairedRegistryDevice(
                            Mac: subKeyName,
                            Name: name,
                            Cod: cod,
                            LeAppearance: leAppearance,
                            BoundSubKeys: subKeys,
                            ContainerId: containerId
                        );
                    }
                }
            }
            catch
            {
                // Registry access denied or unsupported
            }

            return results;
        }

        private static string? TryResolveContainerId(RegistryKey baseKey, string mac, RegistryKey deviceSubKey)
        {
            // 1. Try reading LeContainerId binary GUID from BTHPORT device key (common for BLE)
            if (deviceSubKey.GetValue("LeContainerId") is byte[] leCidBytes && leCidBytes.Length == 16)
            {
                try
                {
                    return new Guid(leCidBytes).ToString("B").ToLowerInvariant();
                }
                catch
                {
                    // Fallback to Enum registry
                }
            }

            // 2. Try reading ContainerID string from Enum\BTHENUM\Dev_<MAC> (Classic BR/EDR)
            string cleanMac = mac.Replace(":", "").Trim().ToUpperInvariant();
            string? enumCid = TryReadContainerIdFromEnum(baseKey, @"SYSTEM\CurrentControlSet\Enum\BTHENUM", cleanMac);
            if (!string.IsNullOrWhiteSpace(enumCid))
            {
                return enumCid;
            }

            // 3. Try reading ContainerID string from Enum\BTHLE\Dev_<MAC> (BLE fallback)
            string? bleCid = TryReadContainerIdFromEnum(baseKey, @"SYSTEM\CurrentControlSet\Enum\BTHLE", cleanMac);
            if (!string.IsNullOrWhiteSpace(bleCid))
            {
                return bleCid;
            }

            return null;
        }

        private static string? TryReadContainerIdFromEnum(RegistryKey baseKey, string enumPath, string cleanMac)
        {
            try
            {
                using var enumKey = baseKey.OpenSubKey(enumPath);
                if (enumKey == null) return null;

                string targetDevPrefix = $"Dev_{cleanMac}";
                var matchingDevSubKey = enumKey.GetSubKeyNames()
                    .FirstOrDefault(k => k.Equals(targetDevPrefix, StringComparison.OrdinalIgnoreCase));

                if (matchingDevSubKey == null) return null;

                using var devKey = enumKey.OpenSubKey(matchingDevSubKey);
                if (devKey == null) return null;

                foreach (var instName in devKey.GetSubKeyNames())
                {
                    using var instKey = devKey.OpenSubKey(instName);
                    if (instKey == null) continue;

                    if (instKey.GetValue("ContainerID") is string cid && !string.IsNullOrWhiteSpace(cid))
                    {
                        return cid.Trim().ToLowerInvariant();
                    }
                }
            }
            catch
            {
                // Suppress registry read exceptions
            }

            return null;
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

        private static string ExtractCoreDeviceName(string rawName)
        {
            int openParen = rawName.IndexOf('(');
            int closeParen = rawName.LastIndexOf(')');
            if (openParen >= 0 && closeParen > openParen)
            {
                string inner = rawName.Substring(openParen + 1, closeParen - openParen - 1);
                string cleanInner = inner
                    .Replace("Hands-Free AG Audio", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("Hands-Free HF Audio", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("Hands-Free Audio", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("Hands-Free", "", StringComparison.OrdinalIgnoreCase)
                    .Replace("HF Audio", "", StringComparison.OrdinalIgnoreCase)
                    .Trim();

                if (!string.IsNullOrWhiteSpace(cleanInner)) return cleanInner;
            }

            return rawName
                .Replace("Hands-Free AG Audio", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Hands-Free HF Audio", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Hands-Free Audio", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Hands-Free", "", StringComparison.OrdinalIgnoreCase)
                .Replace("HF Audio", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Stereo", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Headphones", "", StringComparison.OrdinalIgnoreCase)
                .Replace("Headset", "", StringComparison.OrdinalIgnoreCase)
                .Trim();
        }

        private static string FormatMac(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac) || mac.Length != 12) return mac;
            return string.Join(":", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2).ToUpperInvariant()));
        }
    }
}
