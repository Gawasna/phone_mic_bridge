namespace PhoneMicBridge.Core
{
    public enum BridgeState
    {
        Disconnected,
        Connecting,
        Streaming,
        Paused,
        Stopped,
        Degraded,
        Error
    }
}
