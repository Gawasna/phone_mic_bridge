namespace PhoneMicBridge.Bluetooth
{
    public static class AtCommand
    {
        // Bluetooth Hands-Free Profile standard AT commands
        public const string BRSF_QUERY = "AT+BRSF=1007\r"; // Features: 3-way calling, CLI, Voice recognition, Codec negotiation
        public const string BAC_ENABLE_MSBC = "AT+BAC=1,2\r"; // Codec selection: 1 (CVSD 8kHz), 2 (mSBC 16kHz)
        public const string BCC_OPEN_SCO = "AT+BCC\r"; // Request Bluetooth Codec Connection (Open SCO audio link)
        public const string OK_RESPONSE = "OK";
        public const string ERROR_RESPONSE = "ERROR";

        public static string SetSpeakerVolume(int volumePercent)
        {
            int level = Math.Clamp(volumePercent * 15 / 100, 0, 15);
            return $"AT+VGS={level}\r";
        }

        public static string SetMicrophoneGain(int gainPercent)
        {
            int level = Math.Clamp(gainPercent * 15 / 100, 0, 15);
            return $"AT+VGM={level}\r";
        }
    }
}
