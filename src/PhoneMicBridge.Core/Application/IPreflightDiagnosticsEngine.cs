using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace PhoneMicBridge.Application
{
    /// <summary>
    /// Service contract for running pre-flight diagnostic checks on capture and render audio endpoints
    /// and Bluetooth transport readiness.
    /// </summary>
    public interface IPreflightDiagnosticsEngine
    {
        Task<IReadOnlyList<PreflightItemDto>> ExecuteChecksAsync(
            string? captureDeviceId,
            string? renderDeviceId,
            CancellationToken cancellationToken = default);
    }
}
