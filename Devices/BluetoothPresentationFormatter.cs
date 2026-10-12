using System;
using System.Collections.Generic;
using System.Linq;

namespace PhoneMicBridge.Devices
{
    /// <summary>
    /// Presentation Formatting (UI Policy - Moderate Volatility):
    /// Encapsulates all human-readable status text formatting, MAC formatting,
    /// and canonical label composition displayed on the SubToolbar / UI.
    /// Changing UI copy or status representations modifies only this layer.
    /// </summary>
    public static class BluetoothPresentationFormatter
    {
        public const string StatusConnectedOnline = "Connected";
        public const string StatusOfflineSettings = "Connect in Settings";

        public const string BridgeStatusReady = "READY TO BRIDGE";
        public const string BridgeStatusIncompatibleA2dp = "Incompatible";
        public const string BridgeStatusAudioInactive = "Audio Inactive";
        public const string BridgeStatusPhoneTargetOffline = "HFP Phone";
        public const string BridgeStatusHfpConnected = "HFP Connected";
        public const string BridgeStatusHfpOffline = "HFP Supported";

        public static string FormatConnectionStatus(bool isConnected) =>
            isConnected ? StatusConnectedOnline : StatusOfflineSettings;

        public static string FormatBridgeSupportStatus(
            bool isHfpSupported,
            bool isConnected,
            bool hasHfpEndpoint,
            bool hasActiveEndpoint,
            bool isTargetPhone)
        {
            if (!isHfpSupported)
            {
                return BridgeStatusIncompatibleA2dp;
            }

            if (isConnected && hasHfpEndpoint && hasActiveEndpoint)
            {
                return BridgeStatusReady;
            }

            if (isTargetPhone)
            {
                return isConnected
                    ? BridgeStatusAudioInactive
                    : BridgeStatusPhoneTargetOffline;
            }

            return isConnected
                ? BridgeStatusHfpConnected
                : BridgeStatusHfpOffline;
        }

        public static string FormatCanonicalName(string primaryName, IEnumerable<string> alternateNames)
        {
            var validAliases = alternateNames
                .Where(n => !string.IsNullOrWhiteSpace(n) && !n.Equals(primaryName, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return validAliases.Count > 0
                ? $"{primaryName} ({string.Join(", ", validAliases)})"
                : primaryName;
        }

        public static string FormatMacAddress(string mac)
        {
            if (string.IsNullOrWhiteSpace(mac) || mac.Length != 12) return mac;
            return string.Join(":", Enumerable.Range(0, 6).Select(i => mac.Substring(i * 2, 2).ToUpperInvariant()));
        }
    }
}

