using System;
using System.Collections.Generic;

namespace PhoneMicBridge.Application
{
    public enum EndpointRenderStatus
    {
        Inactive,
        Active,
        Error
    }

    public enum BluetoothTransportStatus
    {
        Disconnected,
        Connecting,
        CodecNegotiating,
        ScoActive,
        Error
    }

    public enum DeliveryAckStatus
    {
        NotApplicable,
        Unconfirmed,
        Confirmed
    }

    public enum DiagnosticOverallStatus
    {
        Unknown,
        InProgress,
        Pass,
        Fail
    }

    /// <summary>
    /// Immutable system state snapshot delivered to the UI.
    /// Eliminates mutable shared state between Audio Thread and UI Dispatcher.
    /// </summary>
    public record BridgeUiSnapshot(
        DateTimeOffset Timestamp,
        Core.BridgeState State,
        string StateDescription,
        string? LastError,
        EndpointRenderStatus RenderStatus,
        BluetoothTransportStatus BluetoothStatus,
        DeliveryAckStatus DeliveryStatus,
        string? SelectedCaptureDeviceId,
        string? SelectedCaptureDeviceName,
        string? SelectedRenderDeviceId,
        string? SelectedRenderDeviceName,
        bool IsMicEnabled,
        float MicGain,
        bool HasAudioFile,
        bool IsFilePlaying,
        string? AudioFileName,
        float FileGain,
        double FileCurrentSeconds,
        double FileTotalSeconds,
        bool IsToneEnabled,
        double ToneFrequency,
        float ToneGain,
        DiagnosticOverallStatus PreflightOverall,
        bool CanStartSession,
        IReadOnlyList<PreflightItemDto> PreflightItems
    )
    {
        public static BridgeUiSnapshot Initial() => new(
            Timestamp: DateTimeOffset.UtcNow,
            State: Core.BridgeState.Disconnected,
            StateDescription: "Ready to connect",
            LastError: null,
            RenderStatus: EndpointRenderStatus.Inactive,
            BluetoothStatus: BluetoothTransportStatus.Disconnected,
            DeliveryStatus: DeliveryAckStatus.Unconfirmed,
            SelectedCaptureDeviceId: null,
            SelectedCaptureDeviceName: null,
            SelectedRenderDeviceId: null,
            SelectedRenderDeviceName: null,
            IsMicEnabled: true,
            MicGain: 1.0f,
            HasAudioFile: false,
            IsFilePlaying: false,
            AudioFileName: null,
            FileGain: 0.7f,
            FileCurrentSeconds: 0,
            FileTotalSeconds: 0,
            IsToneEnabled: false,
            ToneFrequency: 1000.0,
            ToneGain: 0.35f,
            PreflightOverall: DiagnosticOverallStatus.Unknown,
            CanStartSession: false,
            PreflightItems: Array.Empty<PreflightItemDto>()
        );
    }
}
