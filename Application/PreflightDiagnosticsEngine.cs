using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using PhoneMicBridge.Devices;

namespace PhoneMicBridge.Application
{
    /// <summary>
    /// Executes pre-flight diagnostics across capture microphones, render target endpoints,
    /// and Bluetooth transport readiness without blocking the UI dispatcher.
    /// Supports injection of audio device and Bluetooth providers for testability and runtime isolation.
    /// </summary>
    public class PreflightDiagnosticsEngine : IPreflightDiagnosticsEngine
    {
        private readonly Func<List<AudioDeviceInfo>> _captureDevicesProvider;
        private readonly Func<List<AudioDeviceInfo>> _renderDevicesProvider;
        private readonly Func<bool>? _bluetoothRadioStateProvider;

        public PreflightDiagnosticsEngine(
            Func<List<AudioDeviceInfo>>? captureDevicesProvider = null,
            Func<List<AudioDeviceInfo>>? renderDevicesProvider = null,
            Func<bool>? bluetoothRadioStateProvider = null)
        {
            _captureDevicesProvider = captureDevicesProvider ?? (() => DeviceDiscovery.GetCaptureDevices());
            _renderDevicesProvider = renderDevicesProvider ?? (() => DeviceDiscovery.GetRenderDevices());
            _bluetoothRadioStateProvider = bluetoothRadioStateProvider;
        }

        public Task<IReadOnlyList<PreflightItemDto>> ExecuteChecksAsync(
            string? captureDeviceId,
            string? renderDeviceId,
            CancellationToken cancellationToken = default)
        {
            return Task.Run<IReadOnlyList<PreflightItemDto>>(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var items = new List<PreflightItemDto>();

                // 1. Microphone Input Check
                items.Add(CheckMicrophone(captureDeviceId));

                cancellationToken.ThrowIfCancellationRequested();

                // 2. Target Output Device Check
                var (renderItem, targetDevice) = CheckRenderDevice(renderDeviceId);
                items.Add(renderItem);

                cancellationToken.ThrowIfCancellationRequested();

                // 3. Bluetooth Transport Readiness
                items.Add(CheckBluetoothTransport(targetDevice, renderItem.Status));

                return items;
            }, cancellationToken);
        }

        private PreflightItemDto CheckMicrophone(string? captureDeviceId)
        {
            try
            {
                var captureDevices = _captureDevicesProvider();

                if (captureDevices.Count == 0)
                {
                    return new PreflightItemDto(
                        CheckId: "CHECK_MIC_INPUT",
                        Title: "Microphone Input Check",
                        Status: "Fail",
                        Details: "No capture microphone detected in system.",
                        RemediationText: "Check Windows Settings > Privacy & security > Microphone. Ensure microphone is plugged in, activated, and permissions are granted."
                    );
                }

                if (!string.IsNullOrWhiteSpace(captureDeviceId))
                {
                    var selected = captureDevices.FirstOrDefault(d => string.Equals(d.Id, captureDeviceId, StringComparison.OrdinalIgnoreCase));
                    if (selected == null)
                    {
                        return new PreflightItemDto(
                            CheckId: "CHECK_MIC_INPUT",
                            Title: "Microphone Input Check",
                            Status: "Fail",
                            Details: $"Selected microphone (ID: {captureDeviceId}) was not found.",
                            RemediationText: "Reconnect the selected microphone or select another active microphone from the input device dropdown."
                        );
                    }

                    if (selected.State != DeviceState.Active)
                    {
                        return new PreflightItemDto(
                            CheckId: "CHECK_MIC_INPUT",
                            Title: "Microphone Input Check",
                            Status: "Fail",
                            Details: $"Selected microphone '{selected.Name}' is not active (State: {selected.State}).",
                            RemediationText: "Enable the microphone in Windows Sound Control Panel or Windows Settings."
                        );
                    }

                    return new PreflightItemDto(
                        CheckId: "CHECK_MIC_INPUT",
                        Title: "Microphone Input Check",
                        Status: "Pass",
                        Details: $"Active microphone ready: {selected.Name}",
                        RemediationText: "None required."
                    );
                }

                // Default check: must have at least one active device
                var activeDefault = captureDevices.FirstOrDefault(d => d.State == DeviceState.Active);
                if (activeDefault == null)
                {
                    return new PreflightItemDto(
                        CheckId: "CHECK_MIC_INPUT",
                        Title: "Microphone Input Check",
                        Status: "Fail",
                        Details: "Microphone hardware detected, but no input device is in Active state.",
                        RemediationText: "Enable a microphone device in Windows Sound Settings (Settings > System > Sound > Input)."
                    );
                }

                return new PreflightItemDto(
                    CheckId: "CHECK_MIC_INPUT",
                    Title: "Microphone Input Check",
                    Status: "Pass",
                    Details: $"Active microphone ready: {activeDefault.Name}",
                    RemediationText: "None required."
                );
            }
            catch (Exception ex)
            {
                return new PreflightItemDto(
                    CheckId: "CHECK_MIC_INPUT",
                    Title: "Microphone Input Check",
                    Status: "Unknown",
                    Details: $"Microphone check encountered error: {ex.Message}",
                    RemediationText: "Verify Windows Audio service (Audiosrv) is running and user permissions are granted."
                );
            }
        }

        private (PreflightItemDto Item, AudioDeviceInfo? Device) CheckRenderDevice(string? renderDeviceId)
        {
            try
            {
                var renderDevices = _renderDevicesProvider();

                AudioDeviceInfo? target = null;
                if (!string.IsNullOrWhiteSpace(renderDeviceId))
                {
                    target = renderDevices.FirstOrDefault(d => string.Equals(d.Id, renderDeviceId, StringComparison.OrdinalIgnoreCase));
                    if (target == null)
                    {
                        return (new PreflightItemDto(
                            CheckId: "CHECK_RENDER_DEVICE",
                            Title: "Target Output Device Check",
                            Status: "Fail",
                            Details: $"Selected output device (ID: {renderDeviceId}) was not found in system devices.",
                            RemediationText: "Rescan devices or pair and connect your mobile phone via Windows Bluetooth Settings."
                        ), null);
                    }
                }

                if (target == null)
                {
                    // Fall back to preferred HFP device
                    target = renderDevices.FirstOrDefault(d => d.IsTargetHfp && d.State == DeviceState.Active)
                          ?? renderDevices.FirstOrDefault(d => (d.Name.Contains("hands-free", StringComparison.OrdinalIgnoreCase) || d.Name.Contains("handsfree", StringComparison.OrdinalIgnoreCase)) && d.State == DeviceState.Active)
                          ?? renderDevices.FirstOrDefault(d => d.IsTargetHfp);
                }

                if (target == null)
                {
                    return (new PreflightItemDto(
                        CheckId: "CHECK_RENDER_DEVICE",
                        Title: "Target Output Device Check",
                        Status: "Fail",
                        Details: "No Bluetooth Hands-Free audio output device detected.",
                        RemediationText: "Ensure phone Bluetooth is paired and connected to PC. Check that Phone Call audio / Hands-Free profile is enabled on the phone."
                    ), null);
                }

                if (target.State != DeviceState.Active)
                {
                    return (new PreflightItemDto(
                        CheckId: "CHECK_RENDER_DEVICE",
                        Title: "Target Output Device Check",
                        Status: "Fail",
                        Details: $"Target device '{target.Name}' found but inactive (State: {target.State}).",
                        RemediationText: "Connect your paired phone to Windows via Bluetooth Settings to activate the Hands-Free audio device."
                    ), target);
                }

                return (new PreflightItemDto(
                    CheckId: "CHECK_RENDER_DEVICE",
                    Title: "Target Output Device Check",
                    Status: "Pass",
                    Details: $"Target render device ready: {target.Name}",
                    RemediationText: "None required."
                ), target);
            }
            catch (Exception ex)
            {
                return (new PreflightItemDto(
                    CheckId: "CHECK_RENDER_DEVICE",
                    Title: "Target Output Device Check",
                    Status: "Unknown",
                    Details: $"Target render check encountered error: {ex.Message}",
                    RemediationText: "Open Windows Bluetooth settings and verify paired audio devices."
                ), null);
            }
        }

        private PreflightItemDto CheckBluetoothTransport(AudioDeviceInfo? targetDevice, string renderStatus)
        {
            try
            {
                if (string.Equals(renderStatus, "Fail", StringComparison.OrdinalIgnoreCase))
                {
                    return new PreflightItemDto(
                        CheckId: "CHECK_BLUETOOTH_TRANSPORT",
                        Title: "Bluetooth HFP/SCO & Transport Readiness",
                        Status: "Fail",
                        Details: "Bluetooth Hands-Free transport is not established because target audio endpoint is unavailable.",
                        RemediationText: "Verify Bluetooth is enabled in Windows and reconnect your phone in Hands-Free mode."
                    );
                }

                if (string.Equals(renderStatus, "Unknown", StringComparison.OrdinalIgnoreCase))
                {
                    return new PreflightItemDto(
                        CheckId: "CHECK_BLUETOOTH_TRANSPORT",
                        Title: "Bluetooth HFP/SCO & Transport Readiness",
                        Status: "Unknown",
                        Details: "Bluetooth transport readiness could not be determined definitively because target render check was inconclusive.",
                        RemediationText: "Verify Bluetooth adapter is enabled in Windows Device Manager and check paired devices."
                    );
                }

                if (_bluetoothRadioStateProvider != null && !_bluetoothRadioStateProvider())
                {
                    return new PreflightItemDto(
                        CheckId: "CHECK_BLUETOOTH_TRANSPORT",
                        Title: "Bluetooth HFP/SCO & Transport Readiness",
                        Status: "Fail",
                        Details: "Bluetooth radio adapter is disabled or unavailable on this host PC.",
                        RemediationText: "Turn on Bluetooth in Windows Settings (Settings > Bluetooth & devices) or enable the adapter in Device Manager."
                    );
                }

                if (targetDevice != null)
                {
                    bool isHfp = targetDevice.IsTargetHfp
                        || targetDevice.Name.Contains("hands-free", StringComparison.OrdinalIgnoreCase)
                        || targetDevice.Name.Contains("handsfree", StringComparison.OrdinalIgnoreCase);

                    if (!isHfp)
                    {
                        return new PreflightItemDto(
                            CheckId: "CHECK_BLUETOOTH_TRANSPORT",
                            Title: "Bluetooth HFP/SCO & Transport Readiness",
                            Status: "Fail",
                            Details: $"Selected device '{targetDevice.Name}' is not a Bluetooth Hands-Free endpoint (SCO telephony profile not supported).",
                            RemediationText: "Select a Bluetooth Hands-Free AG Audio device from the target render dropdown to enable SCO call audio routing."
                        );
                    }

                    if (targetDevice.State == DeviceState.Active)
                    {
                        return new PreflightItemDto(
                            CheckId: "CHECK_BLUETOOTH_TRANSPORT",
                            Title: "Bluetooth HFP/SCO & Transport Readiness",
                            Status: "Pass",
                            Details: "Bluetooth Hands-Free transport channel ready.",
                            RemediationText: "None required."
                        );
                    }
                }

                return new PreflightItemDto(
                    CheckId: "CHECK_BLUETOOTH_TRANSPORT",
                    Title: "Bluetooth HFP/SCO & Transport Readiness",
                    Status: "Unknown",
                    Details: "Bluetooth transport readiness could not be determined definitively.",
                    RemediationText: "Verify Bluetooth adapter is enabled in Windows Device Manager."
                );
            }
            catch (Exception ex)
            {
                return new PreflightItemDto(
                    CheckId: "CHECK_BLUETOOTH_TRANSPORT",
                    Title: "Bluetooth HFP/SCO & Transport Readiness",
                    Status: "Unknown",
                    Details: $"Bluetooth transport check encountered error: {ex.Message}",
                    RemediationText: "Verify Bluetooth adapter is enabled in Windows Device Manager and check paired devices."
                );
            }
        }
    }
}
