# Phone Mic Bridge

A high-performance Windows Host Audio Bridge and Android Companion system that routes, mixes, and streams PC audio (microphones, audio files, synthesized test tones) to mobile devices via USB ADB and Bluetooth Hands-Free Profile (HFP SCO).

---

## Architecture Overview

```text
┌─────────────────────────────────────────────────────────────┐
│                       WINDOWS HOST                          │
│                                                             │
│   ┌────────────────────────┐    ┌───────────────────────┐   │
│   │   PhoneMicBridge.Gui   │    │   PhoneMicBridge.Cli  │   │
│   │   (src/PhoneMicBridge.Gui)  │   (src/PhoneMicBridge.Cli)│   │
│   └───────────┬────────────┘    └───────────┬───────────┘   │
│               │                             │               │
│               └──────────────┬──────────────┘               │
│                              ▼                              │
│             ┌─────────────────────────────────┐             │
│             │       PhoneMicBridge.Core       │             │
│             │   (src/PhoneMicBridge.Core)     │             │
│             │  WASAPI / Resampler / WinRT BT  │             │
│             └────────────────┬────────────────┘             │
└──────────────────────────────┼──────────────────────────────┘
                               │
            USB ADB Socket (PCM) / Bluetooth SCO
                               │
┌──────────────────────────────▼──────────────────────────────┐
│                    ANDROID COMPANION APP                    │
│                          (android/)                         │
│   ┌─────────────────────────────────────────────────────┐   │
│   │             Audio Routing Service (FG)              │   │
│   │   AdbStreamReceiverServer ◄──► PcmAudioTrackPlayer  │   │
│   │   Telecom BridgeConnectionService (SCO Fallback)    │   │
│   └─────────────────────────────────────────────────────┘   │
└─────────────────────────────────────────────────────────────┘
```

The system comprises two core components:
1. **Windows Host Application (.NET 9.0)**:
   - **`PhoneMicBridge.Core`**: Production Class Library managing low-latency WASAPI loopback capture, high-fidelity audio resampling, mixing pipeline, and WinRT Bluetooth radio interop.
   - **`PhoneMicBridge.Cli`**: Autonomous CLI console runner for headless scripting, diagnostic routing, and tone generation.
   - **`PhoneMicBridge.Gui`**: High-performance WPF Engineering interface with real-time 3-tier metering, device discovery, user profiles, and system tray integration.
2. **Android Companion App (`android/`)**:
   - Native Kotlin Android application (`PMB Companion`) receiving high-fidelity PCM audio over USB ADB port forwarding with sub-20ms latency.
   - 3-State connection supervisor (`DISCONNECTED`, `STANDBY`, `STREAMING_ACTIVE`).
   - Self-managed Telecom `ConnectionService` to enforce persistent Bluetooth SCO routing across Android OEM battery optimizers.

---

## Tech Stack

### Windows Host
- **Platform**: .NET 9.0 (C# 13, Windows 10/11)
- **Audio APIs**: Windows Core Audio (WASAPI Exclusive/Shared), NAudio
- **Bluetooth Subsystem**: Windows.Devices.Bluetooth (WinRT / UWP Interop)
- **UI Framework**: Windows Presentation Foundation (WPF) with Light Engineering Theme
- **Testing**: xUnit with comprehensive concurrency, audio pipeline, and WinRT mocked suites

### Android Companion
- **Platform**: Android SDK 26+ (Target SDK 35)
- **Language**: Kotlin 2.0 with Coroutines and StateFlow
- **Audio Output**: `AudioTrack` with `PERFORMANCE_MODE_LOW_LATENCY`
- **Network Protocol**: TCP Socket receiver over USB ADB forward (`127.0.0.1:34567`)

---

## Getting Started

### Prerequisites
- .NET 9.0 SDK
- Windows 10 (Build 19041+) or Windows 11
- Android Studio with JDK 17/21 and Android SDK Platform 35
- Android phone with USB Debugging enabled

### Build Solution (.NET)
```bash
dotnet build PhoneMicBridge.sln
```

### Run Unit Tests
```bash
dotnet test tests/PhoneMicBridge.Tests/
```

### Run Desktop GUI
```bash
dotnet run --project src/PhoneMicBridge.Gui/PhoneMicBridge.Gui.csproj
```

### Run CLI Engine
```bash
dotnet run --project src/PhoneMicBridge.Cli/PhoneMicBridge.Cli.csproj -- --help
```

### Build Android Companion App
```bash
cd android
./gradlew assembleDebug
```

---

## License
Distributed under the [MIT License](LICENSE).
