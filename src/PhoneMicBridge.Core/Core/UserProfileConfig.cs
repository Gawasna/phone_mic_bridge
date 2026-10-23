using System.Text.Json.Serialization;

namespace PhoneMicBridge.Core
{
    public class UserProfileConfig
    {
        [JsonPropertyName("schema_version")]
        public int SchemaVersion { get; set; } = 1;

        [JsonPropertyName("last_input_device_id")]
        public string? LastInputDeviceId { get; set; }

        [JsonPropertyName("last_target_device_id")]
        public string? LastTargetDeviceId { get; set; }

        [JsonPropertyName("route_mode")]
        public string? RouteMode { get; set; } = "Microphone";

        [JsonPropertyName("mic_gain")]
        public float MicGain { get; set; } = 1.0f;

        [JsonPropertyName("tone_gain")]
        public float ToneGain { get; set; } = 0.5f;

        [JsonPropertyName("file_gain")]
        public float FileGain { get; set; } = 0.8f;
    }

    public interface IProfileRepository
    {
        UserProfileConfig LoadProfile();
        void SaveProfile(UserProfileConfig config);
    }
}
