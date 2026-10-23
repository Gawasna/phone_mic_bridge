using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PhoneMicBridge.Application
{
    /// <summary>
    /// Application boundary service orchestrating audio sessions, state management, device discovery,
    /// pre-flight diagnostics, and level metering.
    /// Acts as the sole boundary between UI Presentation Layer and Low-level Windows CoreAudio infrastructure.
    /// </summary>
    public interface IBridgeApplicationService : IDisposable
    {
        BridgeUiSnapshot CurrentSnapshot { get; }
        event EventHandler<BridgeUiSnapshot>? SnapshotChanged;
        event EventHandler<bool>? RadioStateChanged;
        AudioMeterValues GetCurrentMeterValues();

        Task<BridgeUiSnapshot> RunPreflightDiagnosticsAsync(CancellationToken cancellationToken = default);
        Task<IReadOnlyList<AudioDeviceDto>> GetCaptureDevicesAsync();
        Task<IReadOnlyList<AudioDeviceDto>> GetRenderDevicesAsync();
        Task<IReadOnlyList<BluetoothDeviceDto>> GetBluetoothDevicesAsync() => Task.FromResult<IReadOnlyList<BluetoothDeviceDto>>(Array.Empty<BluetoothDeviceDto>());
        bool IsBluetoothRadioEnabled() => true;
        Task<PhoneMicBridge.Devices.BluetoothRadioStateResult> TurnOnBluetoothRadioAsync() => Task.FromResult(PhoneMicBridge.Devices.BluetoothRadioStateResult.Success);
        Task<PhoneMicBridge.Devices.BluetoothRadioStateResult> SetBluetoothRadioStateAsync(bool enable) => Task.FromResult(PhoneMicBridge.Devices.BluetoothRadioStateResult.Success);
        void OpenBluetoothSettings() { }
        bool OpenWindowsPrivacySettings() => false;
        Task SelectDevicesAsync(string? captureDeviceId, string? renderDeviceId);

        Task StartBridgeAsync(CancellationToken cancellationToken = default);
        Task StopBridgeAsync(CancellationToken cancellationToken = default);

        void SetMicEnabled(bool enabled);
        void SetMicMuted(bool muted) => SetMicEnabled(!muted);
        void ToggleMic() => SetMicEnabled(!CurrentSnapshot.IsMicEnabled);
        void SetMicGain(float gain);

        Task<bool> LoadAudioFileAsync(string filePath, float initialGain = 0.7f);
        void PlayTestAudio(string filePath) => LoadAudioFileAsync(filePath).GetAwaiter().GetResult();
        Task<bool> PlayTestAudioAsync(string filePath) => LoadAudioFileAsync(filePath);
        void StopTestAudio() => ToggleFilePlayback();
        void ToggleFilePlayback();
        void SetFileGain(float gain);

        void SetToneEnabled(bool enabled, double frequency = 1000.0, float initialGain = 0.35f);
        void ToggleTestTone() => SetToneEnabled(!CurrentSnapshot.IsToneEnabled);
        void SetToneGain(float gain);

        void ClearError();
    }
}
