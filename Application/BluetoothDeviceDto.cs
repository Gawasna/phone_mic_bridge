namespace PhoneMicBridge.Application
{
    /// <summary>
    /// Direction of the audio bridge route relative to the host system.
    /// In: Routes host input device (microphone/line-in) into the phone bridge.
    /// Out: Routes host output device (speakers/headphones/playback) into the phone bridge.
    /// </summary>
    public enum BridgeRouteMode
    {
        In,
        Out
    }

    /// <summary>
    /// Classification of discovered Bluetooth peripheral hardware.
    /// </summary>
    public enum BluetoothDeviceCategory
    {
        Phone,
        Headset,
        Speaker,
        Other
    }

    /// <summary>
    /// Type of transport identity for a physical Bluetooth device.
    /// </summary>
    public enum BluetoothTransportType
    {
        Classic,
        Ble,
        DualMode
    }

    /// <summary>
    /// Telemetry representing an individual transport identity bound to a physical Bluetooth device.
    /// </summary>
    public record BluetoothTransportIdentity(
        string MacAddress,
        BluetoothTransportType TransportType,
        string AdvertisedName,
        int? Cod = null,
        int? LeAppearance = null,
        bool HasHfpProfile = false,
        bool HasActiveEndpoint = false
    );

    /// <summary>
    /// Architectural DTO representing a Bluetooth device discovered via Windows Registry and Audio Endpoints,
    /// verified through the 5 Verification Gates:
    /// Gate 1 (Paired) -> Gate 2 (Link State) -> Gate 3 (HFP Support) -> Gate 4 (Bridge Recon) -> Gate 5 (Selection Guard).
    /// Supports Canonical Physical Device Aggregation via ContainerId boundary.
    /// </summary>
    public record BluetoothDeviceDto(
        string Id,
        string Name,
        string MacAddress,
        BluetoothDeviceCategory Category,
        string ConnectionStatus,       // "Connected (Online)", "Offline (Disconnected)"
        string BridgeSupportStatus,    // "READY TO BRIDGE", "HFP Supported (Offline)", "Audio Inactive (Connect in Settings)", "Incompatible (A2DP Only)"
        bool IsConnected,
        bool IsHfpSupported,
        bool IsBridgeReady,
        bool CanSelectForBridge,
        string? ContainerId = null,
        IReadOnlyList<string>? Aliases = null,
        IReadOnlyList<BluetoothTransportIdentity>? TransportIdentities = null
    )
    {
        public string CategoryText => Category switch
        {
            BluetoothDeviceCategory.Phone => "PHONE",
            BluetoothDeviceCategory.Headset => "HEADSET",
            BluetoothDeviceCategory.Speaker => "SPEAKER",
            _ => "OTHER"
        };
    }
}

