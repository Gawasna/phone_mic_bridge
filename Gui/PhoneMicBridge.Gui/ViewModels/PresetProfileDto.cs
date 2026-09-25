using PhoneMicBridge.Application;
using PhoneMicBridge.Core;

namespace PhoneMicBridge.Gui.ViewModels
{
    /// <summary>
    /// Preset configuration profile representing an operational audio setup.
    /// Maps to the Presets Section modeled in Wireframe v0.0.2.
    /// </summary>
    public class PresetProfileDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string Tag { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public float MicGain { get; set; } = 1.0f;
        public BridgeRouteMode RouteMode { get; set; } = BridgeRouteMode.In;
        public bool HighPassFilter { get; set; } = true;
        public bool NoiseGate { get; set; } = true;
    }
}
