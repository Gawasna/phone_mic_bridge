namespace PhoneMicBridge.Core
{
    public record AudioFormat(int SampleRate, int Channels, int BitsPerSample = 32)
    {
        public static readonly AudioFormat BluetoothHfpWideband = new(16000, 1, 16);
        public static readonly AudioFormat BluetoothHfpStandard = new(8000, 1, 16);
        public static readonly AudioFormat StudioQuality = new(48000, 2, 32);
        public static readonly AudioFormat DefaultCapture = new(48000, 1, 32);
    }
}
