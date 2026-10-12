using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace PhoneMicBridge.Devices
{
    public record AudioDeviceInfo(int Index, string Id, string Name, DeviceState State, bool IsTargetHfp);

    public class DeviceDiscovery
    {
        public const string DEFAULT_TARGET_HFP_GUID = "decd7d0e-5d4f-47c4-a7be-4aecc25a7c00";

        public static List<AudioDeviceInfo> GetCaptureDevices()
        {
            var results = new List<AudioDeviceInfo>();
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                results.Add(new AudioDeviceInfo(i, d.ID, d.FriendlyName, d.State, false));
            }

            return results;
        }

        public static List<AudioDeviceInfo> GetRenderDevices(string targetGuid = DEFAULT_TARGET_HFP_GUID)
        {
            var results = new List<AudioDeviceInfo>();
            using var enumerator = new MMDeviceEnumerator();
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);

            for (int i = 0; i < devices.Count; i++)
            {
                var d = devices[i];
                bool isTarget = IsMatchingHfpDevice(d, targetGuid);
                results.Add(new AudioDeviceInfo(i, d.ID, d.FriendlyName, d.State, isTarget));
            }

            return results;
        }

        public static MMDevice? FindTargetRenderDevice(string targetGuid = DEFAULT_TARGET_HFP_GUID)
        {
            var enumerator = new MMDeviceEnumerator();

            // Priority 1: Exact match by targetGuid if active
            if (!string.IsNullOrWhiteSpace(targetGuid))
            {
                try
                {
                    string fullId = "{0.0.0.00000000}.{" + targetGuid.Trim('{', '}') + "}";
                    var exactDevice = enumerator.GetDevice(fullId);
                    if (exactDevice != null && exactDevice.State == DeviceState.Active)
                        return exactDevice;
                }
                catch
                {
                    // Continue to enumeration search
                }

                var allDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);
                foreach (var d in allDevices)
                {
                    if (d.ID.Contains(targetGuid, StringComparison.OrdinalIgnoreCase) && d.State == DeviceState.Active)
                        return d;
                }
            }

            // Priority 2: Any Active Bluetooth HFP Hands-Free endpoint
            var activeDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var d in activeDevices)
            {
                string lowerName = d.FriendlyName.ToLowerInvariant();
                if (lowerName.Contains("hands-free") || lowerName.Contains("handsfree") || lowerName.Contains("bthhfaud"))
                    return d;
            }

            // Priority 3: Target GUID even if not currently active
            try
            {
                string fullId = "{0.0.0.00000000}.{" + targetGuid.Trim('{', '}') + "}";
                return enumerator.GetDevice(fullId);
            }
            catch
            {
                return null;
            }
        }

        public static MMDevice GetDefaultCaptureDevice()
        {
            var enumerator = new MMDeviceEnumerator();
            try
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
            }
            catch
            {
                return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            }
        }

        public static MMDevice FindCaptureOrLoopbackDevice(string? query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return GetDefaultCaptureDevice();
            }

            var enumerator = new MMDeviceEnumerator();

            // 1. Exact match by ID in All active devices
            try
            {
                var exact = enumerator.GetDevice(query);
                if (exact != null && exact.State == DeviceState.Active)
                    return exact;
            }
            catch { }

            // 2. Search Render devices for speaker loopback (e.g. "FxSound", "Realtek")
            var renderDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            foreach (var d in renderDevices)
            {
                if (d.FriendlyName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    d.ID.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }
            }

            // 3. Search Capture devices for microphone
            var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);
            foreach (var d in captureDevices)
            {
                if (d.FriendlyName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    d.ID.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }
            }

            // Fallback: Default capture device
            return GetDefaultCaptureDevice();
        }

        public static MMDevice? FindMatchingHfpCaptureDevice(MMDevice renderDevice)
        {
            if (renderDevice == null) return null;

            var enumerator = new MMDeviceEnumerator();
            var captureDevices = enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active);

            string renderName = renderDevice.FriendlyName;
            string coreName = renderName;
            int openParen = renderName.IndexOf('(');
            int closeParen = renderName.LastIndexOf(')');
            if (openParen >= 0 && closeParen > openParen)
            {
                coreName = renderName.Substring(openParen + 1, closeParen - openParen - 1).Trim();
            }

            // Priority 1: Active capture device matching core name (e.g. "Redmi Note 13 5G Hands-Free HF Audio")
            foreach (var d in captureDevices)
            {
                if (d.FriendlyName.Contains(coreName, StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }
            }

            // Priority 2: Active capture device containing "Hands-Free" or "Handsfree"
            foreach (var d in captureDevices)
            {
                string name = d.FriendlyName;
                if (name.Contains("Hands-Free", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("Handsfree", StringComparison.OrdinalIgnoreCase) ||
                    name.Contains("bthhfaud", StringComparison.OrdinalIgnoreCase))
                {
                    return d;
                }
            }

            return null;
        }

        private static bool IsMatchingHfpDevice(MMDevice device, string targetGuid)
        {
            if (!string.IsNullOrWhiteSpace(targetGuid) && device.ID.Contains(targetGuid, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }
}
