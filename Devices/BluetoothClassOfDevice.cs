using System.Collections.Generic;

namespace PhoneMicBridge.Devices
{
    /// <summary>
    /// Bluetooth SIG Assigned Numbers representation of Class of Device (CoD).
    /// Specification Bitfield Representation (Domain Standard - Immutable).
    /// Enforces strict separation between Service Class capability flags and Device Type (Major + Minor).
    /// </summary>
    public readonly record struct BluetoothClassOfDevice(int RawValue)
    {
        // 24-bit CoD Layout: [23..13 Service Classes (11b)] [12..8 Major Device (5b)] [7..2 Minor Device (6b)] [1..0 Format (2b)]
        public int FormatType => RawValue & 0x03; // Bits 0-1 (Always 00b)
        public int MinorDeviceClass => (RawValue >> 2) & 0x3F; // Bits 2-7
        public int MajorDeviceClass => (RawValue >> 8) & 0x1F; // Bits 8-12
        public int MajorServiceClasses => (RawValue >> 13) & 0x7FF; // Bits 13-23

        // Bluetooth SIG Major Device Classes (Bits 8-12)
        public const int MajorMiscellaneous = 0x00;
        public const int MajorComputer      = 0x01;
        public const int MajorPhone         = 0x02;
        public const int MajorNetworkAp     = 0x03;
        public const int MajorAudioVideo    = 0x04;
        public const int MajorPeripheral    = 0x05;
        public const int MajorImaging       = 0x06;
        public const int MajorWearable      = 0x07;

        // Bluetooth SIG Minor Device Classes for Audio/Video (Major = 0x04)
        public const int MinorAudioWearableHeadset = 0x01; // 000001b (Wearable Headset Device with Mic)
        public const int MinorAudioHandsFree      = 0x02; // 000010b (Hands-free unit)
        public const int MinorAudioMicrophone     = 0x04; // 000100b (Standalone Microphone)
        public const int MinorAudioLoudspeaker    = 0x05; // 000101b (Loudspeaker)
        public const int MinorAudioHeadphones     = 0x06; // 000110b (Headphones / Stereo Playback)
        public const int MinorAudioPortable       = 0x07; // 000111b (Portable Audio)

        // Bluetooth SIG Minor Device Classes for Phone (Major = 0x02)
        public const int MinorPhoneCellular   = 0x01;
        public const int MinorPhoneCordless   = 0x02;
        public const int MinorPhoneSmartphone = 0x03;
        public const int MinorPhoneModem      = 0x04;

        // Classification Rules based strictly on (Major, Minor) pairs - NOT service bits alone!
        public bool IsConfirmedLoudspeaker =>
            MajorDeviceClass == MajorAudioVideo &&
            (MinorDeviceClass == MinorAudioLoudspeaker || MinorDeviceClass == MinorAudioHeadphones || MinorDeviceClass == MinorAudioPortable);

        public bool IsConfirmedHeadset =>
            MajorDeviceClass == MajorAudioVideo &&
            (MinorDeviceClass == MinorAudioWearableHeadset || MinorDeviceClass == MinorAudioHandsFree);

        public bool IsConfirmedPhone =>
            MajorDeviceClass == MajorPhone &&
            (MinorDeviceClass == MinorPhoneCellular || MinorDeviceClass == MinorPhoneSmartphone || MinorDeviceClass == MinorPhoneCordless);

        // Major Service Class Bitfield Queries (Contextual Capabilities only)
        public bool HasTelephonyService => (MajorServiceClasses & (1 << (22 - 13))) != 0; // Bit 22
        public bool HasAudioService     => (MajorServiceClasses & (1 << (21 - 13))) != 0; // Bit 21
        public bool HasCapturingService => (MajorServiceClasses & (1 << (19 - 13))) != 0; // Bit 19
        public bool HasRenderingService => (MajorServiceClasses & (1 << (18 - 13))) != 0; // Bit 18
    }

    /// <summary>
    /// Explicit paired device metadata harvested from Windows BTHPORT Registry.
    /// Immutable domain transfer record.
    /// </summary>
    public record PairedRegistryDevice(
        string Mac,
        string Name,
        int? Cod = null,
        int? LeAppearance = null,
        IReadOnlyList<string>? BoundSubKeys = null,
        string? ContainerId = null
    );
}
