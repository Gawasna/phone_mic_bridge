using System;

namespace PhoneMicBridge.Core
{
    public interface IAudioEngine : IDisposable
    {
        BridgeState State { get; }
        event EventHandler<BridgeState>? StateChanged;
        void Start();
        void Stop();
    }
}
