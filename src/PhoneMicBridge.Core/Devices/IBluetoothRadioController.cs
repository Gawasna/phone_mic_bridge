using System;
using System.Threading.Tasks;

namespace PhoneMicBridge.Devices
{
    /// <summary>
    /// Outcome of programmatic Bluetooth radio state transition requests.
    /// Captures OS-level access control and system policy boundaries.
    /// </summary>
    public enum BluetoothRadioStateResult
    {
        /// <summary>
        /// Radio state successfully transitioned.
        /// </summary>
        Success,

        /// <summary>
        /// Radio is already in the requested state.
        /// </summary>
        AlreadyInDesiredState,

        /// <summary>
        /// Denied due to user privacy consent policy in Windows Settings.
        /// </summary>
        DeniedByUser,

        /// <summary>
        /// Denied by system policy (Group Policy or system capability constraint).
        /// </summary>
        DeniedBySystem,

        /// <summary>
        /// No Bluetooth radio adapter was discovered on the host system.
        /// </summary>
        RadioNotFound,

        /// <summary>
        /// Programmatic radio control is not supported on this platform runtime.
        /// </summary>
        NotSupported,

        /// <summary>
        /// Unexpected runtime failure or RPC error during transition.
        /// </summary>
        Failed
    }

    /// <summary>
    /// Contract for low-level OS radio hardware management mechanism.
    /// Separates programmatic radio control (WinRT mechanism) from application routing policy.
    /// </summary>
    public interface IBluetoothRadioController
    {
        /// <summary>
        /// Event fired whenever the host Bluetooth radio hardware power state changes.
        /// </summary>
        event EventHandler<bool>? RadioStateChanged;

        /// <summary>
        /// Synchronously checks if Bluetooth radio adapter is enabled without triggering sync-over-async deadlocks.
        /// Reflects actual RadioState (On / Off).
        /// </summary>
        bool IsRadioEnabled();

        /// <summary>
        /// Asynchronously checks if a Bluetooth radio is currently powered on.
        /// </summary>
        Task<bool> IsRadioEnabledAsync();

        /// <summary>
        /// Programmatically sets Bluetooth radio power state (On / Off) without forcing user navigation to Settings.
        /// Honors Windows security and privacy permissions.
        /// </summary>
        Task<BluetoothRadioStateResult> SetRadioStateAsync(bool enable);

        /// <summary>
        /// Fallback mechanism to launch native Windows Bluetooth Settings URI.
        /// </summary>
        bool OpenWindowsBluetoothSettings();

        /// <summary>
        /// Fallback mechanism to launch Windows Radios Privacy Settings URI when access is denied.
        /// </summary>
        bool OpenWindowsPrivacySettings();
    }
}
