using System;
using System.Collections.Generic;
using System.Linq;
using PhoneMicBridge.Application;

namespace PhoneMicBridge.Devices
{
    /// <summary>
    /// Contract for aggregating disparate transport identities into a canonical physical device.
    /// </summary>
    public interface IBluetoothDeviceAggregator
    {
        List<BluetoothDeviceDto> AggregateByContainerId(IEnumerable<BluetoothDeviceDto> rawDevices);
    }

    /// <summary>
    /// Entity Aggregation (Chassis ContainerId Boundary Policy):
    /// Merges multiple transport identities (e.g., Classic BR/EDR 'DRM25' and BLE RPA 'Redmi Note 13 5G')
    /// that share the exact same physical chassis ContainerId into a single Canonical Device DTO.
    /// Strictly enforces OS Single Chassis Boundary: Devices with different or null ContainerIds
    /// are NEVER merged.
    /// </summary>
    public class BluetoothDeviceAggregator : IBluetoothDeviceAggregator
    {
        public static readonly BluetoothDeviceAggregator Default = new();

        public List<BluetoothDeviceDto> AggregateByContainerId(IEnumerable<BluetoothDeviceDto> rawDevices)
        {
            var rawList = rawDevices.ToList();
            var canonicalDevices = new List<BluetoothDeviceDto>();
            var groupedByContainer = rawList
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
                    string canonicalName = BluetoothPresentationFormatter.FormatCanonicalName(primary.Name, aliasList);

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
                        bridgeStatus = BluetoothPresentationFormatter.BridgeStatusReady;
                    }
                    else if (category == BluetoothDeviceCategory.Phone)
                    {
                        bridgeStatus = anyConnected
                            ? BluetoothPresentationFormatter.BridgeStatusAudioInactive
                            : BluetoothPresentationFormatter.BridgeStatusPhoneTargetOffline;
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
            foreach (var dev in rawList)
            {
                if (!processedIds.Contains(dev.Id))
                {
                    canonicalDevices.Add(dev);
                }
            }

            return canonicalDevices;
        }
    }
}
