namespace PhoneMicBridge.Application
{
    /// <summary>
    /// Pure DTO for audio endpoint information without exposing COM MMDevice references to presentation layer.
    /// Uses stable MMDevice ID for idempotent device selection.
    /// </summary>
    public record AudioDeviceDto(
        string Id,
        string Name,
        bool IsActive,
        bool IsTargetHfp,
        bool IsDefault
    );
}
