using System;
using System.Collections.Generic;
using System.Linq;
using PhoneMicBridge.Application;

namespace PhoneMicBridge.Devices
{
    /// <summary>
    /// Contract for Bluetooth device archetype classification and naming heuristic policy.
    /// This layer has high volatility as new phone models, keywords, and headsets are added.
    /// </summary>
    public interface IBluetoothDeviceClassifier
    {
        BluetoothDeviceCategory ClassifyDeviceArchetype(
            PairedRegistryDevice regDev,
            IReadOnlyList<AudioDeviceInfo> matchedEndpoints,
            string rawName);

        BluetoothDeviceCategory DetermineCategoryHeuristic(string name);
        string ExtractCoreDeviceName(string rawName);
    }

    /// <summary>
    /// Classification & Heuristic Matching (Policy - High Volatility):
    /// Encapsulates all domain heuristics, token filtering, phone/headset/speaker keyword matching,
    /// and the 3-Tier Evidence-Based Classification architecture.
    /// Adding new phone models (e.g., Pixel 9, Xiaomi 14) modifies ONLY this policy file,
    /// completely eliminating blast radius to the Win32 Registry Mechanism.
    /// </summary>
    public class BluetoothDeviceClassifier : IBluetoothDeviceClassifier
    {
        private static readonly string[] DefaultPhoneKeywords =
        {
            "redmi", "galaxy", "iphone", "pixel", "xiaomi", "oppo", "vivo", "joy", "phone", "huawei", "realme", "oneplus", "samsung", "motorola", "sony xperia"
        };

        private static readonly string[] DefaultHeadsetKeywords =
        {
            "headphone", "headset", "buds", "airpod", "tws", "sy-t25", "m18", "m28", "m38", "earphone", "realfit"
        };

        private static readonly string[] DefaultSpeakerKeywords =
        {
            "speaker", "music", "soundbar", "bt-speaker"
        };

        public static readonly BluetoothDeviceClassifier Default = new();

        public IReadOnlyList<string> PhoneKeywords { get; }
        public IReadOnlyList<string> HeadsetKeywords { get; }
        public IReadOnlyList<string> SpeakerKeywords { get; }

        public BluetoothDeviceClassifier(
            IEnumerable<string>? customPhoneKeywords = null,
            IEnumerable<string>? customHeadsetKeywords = null,
            IEnumerable<string>? customSpeakerKeywords = null)
        {
            PhoneKeywords = (customPhoneKeywords != null
                ? DefaultPhoneKeywords.Concat(customPhoneKeywords).Distinct(StringComparer.OrdinalIgnoreCase)
                : DefaultPhoneKeywords).ToList();

            HeadsetKeywords = (customHeadsetKeywords != null
                ? DefaultHeadsetKeywords.Concat(customHeadsetKeywords).Distinct(StringComparer.OrdinalIgnoreCase)
                : DefaultHeadsetKeywords).ToList();

            SpeakerKeywords = (customSpeakerKeywords != null
                ? DefaultSpeakerKeywords.Concat(customSpeakerKeywords).Distinct(StringComparer.OrdinalIgnoreCase)
                : DefaultSpeakerKeywords).ToList();
        }

        /// <summary>
        /// 3-Tier Classification Engine:
        /// Tier 1: Bluetooth SIG CoD (Major + Minor) explicit metadata.
        /// Tier 2: OS CoreAudio PnP Endpoint representations (Hands-Free HF Audio).
        /// Tier 3: Heuristic Tokenized Naming Fallback.
        /// </summary>
        public BluetoothDeviceCategory ClassifyDeviceArchetype(
            PairedRegistryDevice regDev,
            IReadOnlyList<AudioDeviceInfo> matchedEndpoints,
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

        public BluetoothDeviceCategory DetermineCategoryHeuristic(string name)
        {
            var lower = name.ToLowerInvariant();
            if (HeadsetKeywords.Any(k => lower.Contains(k))) return BluetoothDeviceCategory.Headset;
            if (SpeakerKeywords.Any(k => lower.Contains(k))) return BluetoothDeviceCategory.Speaker;
            if (PhoneKeywords.Any(k => lower.Contains(k))) return BluetoothDeviceCategory.Phone;
            return BluetoothDeviceCategory.Other;
        }

        public string ExtractCoreDeviceName(string rawName)
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
    }
}
