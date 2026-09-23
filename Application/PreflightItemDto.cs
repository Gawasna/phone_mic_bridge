namespace PhoneMicBridge.Application
{
    /// <summary>
    /// Represents an individual pre-flight diagnostic check result with tri-state status and explicit remediation text.
    /// Status values: "Pass", "Fail", "Unknown".
    /// </summary>
    public record PreflightItemDto(
        string CheckId,
        string Title,
        string Status,
        string Details,
        string RemediationText
    );
}
