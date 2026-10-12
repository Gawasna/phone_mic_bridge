using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.Win32;

namespace PhoneMicBridge.Devices
{
    /// <summary>
    /// Contract for low-level OS hardware access mechanism to Bluetooth Registry information.
    /// Mechanism layer has extremely low volatility.
    /// </summary>
    public interface IBluetoothRegistryReader
    {
        Dictionary<string, PairedRegistryDevice> ReadPairedDevices();
        bool IsBluetoothRadioEnabled();
        bool OpenWindowsBluetoothSettings();
    }

    /// <summary>
    /// Low-Level OS Hardware Access (Mechanism - Extremely Low Volatility):
    /// Reads Win32 Registry via Microsoft.Win32, enumerates SubKeys under BTHPORT, BTHENUM, BTHLE,
    /// parses UTF-8 byte arrays, and decodes binary 16-byte LeContainerId GUIDs.
    /// Strictly isolates hardware mechanism from heuristic classification and presentation policies.
    /// </summary>
    public class BluetoothRegistryReader : IBluetoothRegistryReader
    {
        public const string BthRegistryPath = @"SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices";

        public static readonly BluetoothRegistryReader Instance = new();

        /// <summary>
        /// Reads all paired Bluetooth devices and their raw hardware metadata from Windows Registry.
        /// </summary>
        public Dictionary<string, PairedRegistryDevice> ReadPairedDevices()
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
                // Registry access denied or unsupported on current environment
            }

            return results;
        }

        /// <summary>
        /// Checks whether Bluetooth Radio is enabled by checking existence of the BTHPORT device key.
        /// </summary>
        public bool IsBluetoothRadioEnabled()
        {
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

        /// <summary>
        /// Launches the native Windows Bluetooth Settings URI via shell execution.
        /// </summary>
        public bool OpenWindowsBluetoothSettings()
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
    }
}
