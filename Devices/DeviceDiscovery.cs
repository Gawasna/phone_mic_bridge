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
            var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.All);

            foreach (var d in devices)
            {
                if (IsMatchingHfpDevice(d, targetGuid))
                    return d;
            }

            try
            {
                string fullId = "{0.0.0.00000000}.{" + targetGuid + "}";
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
            if (device.ID.Contains(targetGuid, StringComparison.OrdinalIgnoreCase))
                return true;

            // Fallback detection for Bluetooth HFP hands-free audio endpoints
            string lowerName = device.FriendlyName.ToLowerInvariant();
            if (lowerName.Contains("hands-free") || lowerName.Contains("handsfree") || lowerName.Contains("bthhfaud"))
                return true;

            return false;
        }
    }
}
