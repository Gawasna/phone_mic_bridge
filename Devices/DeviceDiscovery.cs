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

        private static bool IsMatchingHfpDevice(MMDevice device, string targetGuid)
        {
            if (!string.IsNullOrWhiteSpace(targetGuid) && device.ID.Contains(targetGuid, StringComparison.OrdinalIgnoreCase))
                return true;

            return false;
        }
    }
}
