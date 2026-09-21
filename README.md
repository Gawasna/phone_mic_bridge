# PhoneMicBridge

A high-performance Windows Host Audio Bridge that routes, mixes, and streams PC audio (physical microphones, audio files, synthesized test tones) to phone/external audio endpoints (via Bluetooth Hands-Free Profile / HFP SCO or streaming protocols).

## Architecture & Roadmap

The project is structured into modular development phases:
- **Phase 1 (Active)**: Host Bridge Core & Low-latency Audio Engine (WASAPI, Resampling, Mixing Pipeline, Device Discovery).
- **Phase 2**: Autonomous Bluetooth HFP/SCO Channel Controller (Direct AT commands / RFCOMM without third-party app dependencies).
- **Phase 3**: Streaming Transport Protocol Layer (Low-latency PCM / Opus socket server over ADB or Wi-Fi).
- **Phase 4 (Deferred)**: Android Audio Receiver Client.
- **Phase 5**: Desktop GUI & Packaging.

## Tech Stack
- .NET 9.0 (C# 13, Windows Audio APIs)
- NAudio & NAudio.Wasapi (WASAPI low-latency audio capture and render)

## Getting Started

### Prerequisites
- .NET 9.0 SDK
- Windows 10/11 (WASAPI endpoints)

### Build
```bash
dotnet build PhoneMicBridge.sln
```

### Run CLI
```bash
dotnet run --project phone_mic_bridge.csproj -- --help
```

## License
Distributed under the [MIT License](LICENSE).
