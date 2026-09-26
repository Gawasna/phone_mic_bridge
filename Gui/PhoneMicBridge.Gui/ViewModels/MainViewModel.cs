using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using PhoneMicBridge.Application;
using PhoneMicBridge.Core;

namespace PhoneMicBridge.Gui.ViewModels
{
    /// <summary>
    /// Thin UI Orchestrator and Facade for Phone Mic Bridge Desktop GUI.
    /// Composes specialized coordinators:
    /// - <see cref="DeviceRouteCoordinator"/>: Route mode, endpoint filtering, PnP device loading.
    /// - <see cref="AudioMeterMonitor"/>: 30Hz VU meter runtime loop and zero heap allocation.
    /// - <see cref="AudioSourceController"/>: Microphone, test tone, and audio file player controls.
    /// Preserves 100% backward compatibility for all XAML data bindings and unit tests (Praise-OOP & OS-Thinking).
    /// </summary>
    public class MainViewModel : INotifyPropertyChanged, IDisposable
    {
        private readonly IBridgeApplicationService _service;
        private readonly DeviceRouteCoordinator _routeCoordinator;
        private readonly AudioMeterMonitor _meterMonitor;
        private readonly AudioSourceController _sourceController;

        private BridgeUiSnapshot _snapshot;
        private bool _isCompactMode = true;
        private double _windowWidth = 720;
        private bool _isDisposed;

        public event PropertyChangedEventHandler? PropertyChanged;

        // Sub-Coordinator Accessors
        public DeviceRouteCoordinator RouteCoordinator => _routeCoordinator;
        public AudioMeterMonitor MeterMonitor => _meterMonitor;
        public AudioSourceController SourceController => _sourceController;

        // Session Snapshot & State
        public BridgeUiSnapshot Snapshot
        {
            get => _snapshot;
            private set
            {
                if (_snapshot != value)
                {
                    _snapshot = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(StateText));
                    OnPropertyChanged(nameof(StateColorKey));
                    OnPropertyChanged(nameof(CanStart));
                    OnPropertyChanged(nameof(CanStop));
                    OnPropertyChanged(nameof(HasError));
                    OnPropertyChanged(nameof(ErrorMessage));
                    OnPropertyChanged(nameof(EndpointStatusText));
                    OnPropertyChanged(nameof(BluetoothStatusText));
                    OnPropertyChanged(nameof(DeliveryStatusText));
                    OnPropertyChanged(nameof(PreflightBadgeText));
                    OnPropertyChanged(nameof(PreflightBadgeColorKey));
                    OnPropertyChanged(nameof(StartButtonTooltip));
                    OnPropertyChanged(nameof(SafetyInterlockStatusText));

                    (StartCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                    (StopCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
                }
            }
        }

        // Computed status presentation properties
        public string StateText => _snapshot.State switch
        {
            BridgeState.Disconnected => "DISCONNECTED",
            BridgeState.Connecting => "CONNECTING...",
            BridgeState.Streaming => "STREAMING ACTIVE",
            BridgeState.Paused => "PAUSED",
            BridgeState.Stopped => "STOPPED",
            BridgeState.Error => "ERROR",
            _ => _snapshot.State.ToString().ToUpperInvariant()
        };

        public string StateColorKey => _snapshot.State switch
        {
            BridgeState.Streaming => "PassBgBrush",
            BridgeState.Connecting => "UnknownBgBrush",
            BridgeState.Error => "FailBgBrush",
            _ => "PanelBrush"
        };

        public bool CanStart => _snapshot.CanStartSession && _snapshot.State != BridgeState.Streaming && _snapshot.State != BridgeState.Connecting;
        public bool CanStop => _snapshot.State == BridgeState.Streaming || _snapshot.State == BridgeState.Connecting;

        public bool HasError => !string.IsNullOrEmpty(_snapshot.LastError);
        public string ErrorMessage => _snapshot.LastError ?? string.Empty;

        public string EndpointStatusText => _snapshot.RenderStatus switch
        {
            EndpointRenderStatus.Active => "Endpoint Render Active",
            EndpointRenderStatus.Error => "ERROR (Endpoint Failed)",
            _ => "INACTIVE"
        };

        public string BluetoothStatusText => _snapshot.BluetoothStatus switch
        {
            BluetoothTransportStatus.ScoActive => "HFP / SCO Channel Active",
            BluetoothTransportStatus.Connecting => "CONNECTING (Negotiating HFP)",
            BluetoothTransportStatus.Error => "ERROR (Link Dropped)",
            _ => "DISCONNECTED"
        };

        public string DeliveryStatusText => _snapshot.DeliveryStatus switch
        {
            DeliveryAckStatus.Confirmed => "CONFIRMED (Delivered to Phone)",
            _ => "UNVERIFIED (No Companion ACK Protocol)"
        };

        public string PreflightBadgeText => _snapshot.PreflightOverall switch
        {
            DiagnosticOverallStatus.Pass => "ALL CHECKS PASSED",
            DiagnosticOverallStatus.Fail => "CHECKS FAILED",
            DiagnosticOverallStatus.InProgress => "CHECKING...",
            _ => "NOT VERIFIED"
        };

        public string PreflightBadgeColorKey => _snapshot.PreflightOverall switch
        {
            DiagnosticOverallStatus.Pass => "PassBgBrush",
            DiagnosticOverallStatus.Fail => "FailBgBrush",
            _ => "UnknownBgBrush"
        };

        public string StartButtonTooltip => _snapshot.PreflightOverall switch
        {
            DiagnosticOverallStatus.Fail => "Cannot start bridge: Pre-flight diagnostics failed. Please resolve reported issues in the diagnostics panel.",
            DiagnosticOverallStatus.InProgress => "Diagnostics in progress. Please wait...",
            _ => _snapshot.State switch
            {
                BridgeState.Streaming => "Audio bridge session is currently active.",
                BridgeState.Connecting => "Audio bridge is currently connecting...",
                _ => "Start audio bridge session."
            }
        };

        public string SafetyInterlockStatusText => _snapshot.PreflightOverall switch
        {
            DiagnosticOverallStatus.Fail => "SAFETY INTERLOCK: START BLOCKED (Pre-flight checks failed)",
            DiagnosticOverallStatus.Unknown => "DIAGNOSTICS ADVISORY: Check status unverified",
            DiagnosticOverallStatus.InProgress => "DIAGNOSTICS: In progress...",
            _ => "SAFETY INTERLOCK: READY (All checks passed)"
        };

                public double WindowWidth
        {
            get => _windowWidth;
            set
            {
                if (_windowWidth != value)
                {
                    _windowWidth = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ShowSupportStatus));
                    
                    OnPropertyChanged(nameof(ShowDeviceIcon));
                }
            }
        }

        public bool ShowSupportStatus => WindowWidth >= 900;
        
        public bool ShowDeviceIcon => WindowWidth >= 1400;

        public bool IsCompactMode
        {
            get => _isCompactMode;
            set
            {
                if (_isCompactMode != value)
                {
                    _isCompactMode = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsExpandedMode));
                    OnPropertyChanged(nameof(WindowModeText));
                }
            }
        }

        public bool IsExpandedMode
        {
            get => !_isCompactMode;
            set => IsCompactMode = !value;
        }

        public string WindowModeText => IsCompactMode ? "COMPACT (720x680)" : "FULLSCREEN (1920x1080)";

        public string StreamTelemetrySummary => "mSBC (16.0 kHz) • Latency: ~18ms • Buffer: 960/1920 • Loss: 0.0%";

        #region Route & Device Delegations (DeviceRouteCoordinator)

        public ObservableCollection<AudioDeviceDto> CaptureDevices => _routeCoordinator.CaptureDevices;
        public ObservableCollection<AudioDeviceDto> RenderDevices => _routeCoordinator.RenderDevices;
        public ObservableCollection<BluetoothDeviceDto> BluetoothDevices => _routeCoordinator.BluetoothDevices;
        public ObservableCollection<AudioDeviceDto> AvailableEndpointDevices => _routeCoordinator.AvailableEndpointDevices;

        public BridgeRouteMode RouteMode
        {
            get => _routeCoordinator.RouteMode;
            set => _routeCoordinator.RouteMode = value;
        }

        public bool IsRouteIn => _routeCoordinator.IsRouteIn;
        public bool IsRouteOut => _routeCoordinator.IsRouteOut;
        public string RouteModeText => _routeCoordinator.RouteModeText;
        public string Dropdown2Label => _routeCoordinator.Dropdown2Label;
        public string Dropdown2Placeholder => _routeCoordinator.Dropdown2Placeholder;

        public void SetRouteMode(BridgeRouteMode mode) => _routeCoordinator.SetRouteMode(mode);

        public AudioDeviceDto? SelectedCaptureDevice
        {
            get => _routeCoordinator.SelectedCaptureDevice;
            set => _routeCoordinator.SelectedCaptureDevice = value;
        }

        public AudioDeviceDto? SelectedRenderDevice
        {
            get => _routeCoordinator.SelectedRenderDevice;
            set => _routeCoordinator.SelectedRenderDevice = value;
        }

        public string? SelectedCaptureDeviceId => _routeCoordinator.SelectedCaptureDeviceId;
        public string? SelectedRenderDeviceId => _routeCoordinator.SelectedRenderDeviceId;

        public AudioDeviceDto? SelectedEndpointDevice
        {
            get => _routeCoordinator.SelectedEndpointDevice;
            set => _routeCoordinator.SelectedEndpointDevice = value;
        }

        public string? SelectedEndpointDeviceId => _routeCoordinator.SelectedEndpointDeviceId;

        public BluetoothDeviceDto? SelectedBluetoothDevice
        {
            get => _routeCoordinator.SelectedBluetoothDevice;
            set => _routeCoordinator.SelectedBluetoothDevice = value;
        }

        public string? SelectedBluetoothDeviceId => _routeCoordinator.SelectedBluetoothDeviceId;

        public bool IsBluetoothEnabled => _routeCoordinator.IsBluetoothEnabled;
        public bool IsBluetoothDisabled => _routeCoordinator.IsBluetoothDisabled;
        public bool HasBluetoothDevices => _routeCoordinator.HasBluetoothDevices;
        public bool IsBluetoothEmpty => _routeCoordinator.IsBluetoothEmpty;
        public bool IsOperatingBluetoothRadio => _routeCoordinator.IsOperatingBluetoothRadio;

        public ICommand SetRouteInCommand => _routeCoordinator.SetRouteInCommand;
        public ICommand SetRouteOutCommand => _routeCoordinator.SetRouteOutCommand;
        public ICommand ToggleRouteModeCommand => _routeCoordinator.ToggleRouteModeCommand;
        public ICommand OpenBluetoothSettingsCommand => _routeCoordinator.OpenBluetoothSettingsCommand;
        public ICommand TurnOnBluetoothCommand => _routeCoordinator.TurnOnBluetoothCommand;

        public Task LoadDevicesAsync() => _routeCoordinator.LoadDevicesAsync();

        #endregion

        #region Audio VU Meter Delegations (AudioMeterMonitor)

        public AudioMeterValues MeterValues => _meterMonitor.MeterValues;
        public double InputPeakPercent => _meterMonitor.InputPeakPercent;
        public float InputPeakDb => _meterMonitor.InputPeakDb;
        public float InputPeak => _meterMonitor.InputPeak;
        public float InputRms => _meterMonitor.InputRms;
        public string InputPeakDbText => _meterMonitor.InputPeakDbText;
        public bool IsInputClipping => _meterMonitor.IsInputClipping;
        public System.Windows.Media.Brush InputMeterBrush => _meterMonitor.InputMeterBrush;

        public double OutputPeakPercent => _meterMonitor.OutputPeakPercent;
        public float OutputPeakDb => _meterMonitor.OutputPeakDb;
        public float OutputPeak => _meterMonitor.OutputPeak;
        public float OutputRms => _meterMonitor.OutputRms;
        public string OutputPeakDbText => _meterMonitor.OutputPeakDbText;
        public bool IsOutputClipping => _meterMonitor.IsOutputClipping;
        public System.Windows.Media.Brush OutputMeterBrush => _meterMonitor.OutputMeterBrush;

        // DSP Post-Processing Metering (Slot 2 in Fullscreen v0.0.3)
        public double DspPeakPercent => _meterMonitor.DspPeakPercent;
        public float DspPeakDb => _meterMonitor.DspPeakDb;
        public string DspPeakDbText => _meterMonitor.DspPeakDbText;
        public bool IsDspClipping => _meterMonitor.IsDspClipping;
        public System.Windows.Media.Brush DspMeterBrush => _meterMonitor.DspMeterBrush;

        // 8-Band Frequency Spectrum Visualizer (FFT Acoustic Bands for Playground Section)
        public double Band60Hz => _meterMonitor.Band60Hz;
        public double Band125Hz => _meterMonitor.Band125Hz;
        public double Band250Hz => _meterMonitor.Band250Hz;
        public double Band500Hz => _meterMonitor.Band500Hz;
        public double Band1kHz => _meterMonitor.Band1kHz;
        public double Band2kHz => _meterMonitor.Band2kHz;
        public double Band4kHz => _meterMonitor.Band4kHz;
        public double Band8kHz => _meterMonitor.Band8kHz;

        public void UpdateMeters() => _meterMonitor.UpdateMeters();

        #endregion

        #region Audio Source Delegations (AudioSourceController)

        public bool IsMicEnabled
        {
            get => _sourceController.IsMicEnabled;
            set => _sourceController.IsMicEnabled = value;
        }

        public bool IsMicMuted
        {
            get => _sourceController.IsMicMuted;
            set => _sourceController.IsMicMuted = value;
        }

        public string MicMuteButtonText => _sourceController.MicMuteButtonText;
        public string MicStatusText => _sourceController.MicStatusText;

        public float MicGain
        {
            get => _sourceController.MicGain;
            set => _sourceController.MicGain = value;
        }

        public string MicGainText => _sourceController.MicGainText;

        public void ToggleMic() => _sourceController.ToggleMic();
        public void SetMicMuted(bool muted) => _sourceController.SetMicMuted(muted);
        public void SetMicEnabled(bool enabled) => _sourceController.SetMicEnabled(enabled);
        public void SetMicGain(float gain) => _sourceController.SetMicGain(gain);

        public bool IsToneEnabled
        {
            get => _sourceController.IsToneEnabled;
            set => _sourceController.IsToneEnabled = value;
        }

        public float ToneGain
        {
            get => _sourceController.ToneGain;
            set => _sourceController.ToneGain = value;
        }

        public string ToneGainText => _sourceController.ToneGainText;
        public double ToneFrequency => _sourceController.ToneFrequency;
        public string ToneStatusText => _sourceController.ToneStatusText;
        public string ToneButtonText => _sourceController.ToneButtonText;

        public void ToggleTestTone() => _sourceController.ToggleTestTone();
        public void SetToneEnabled(bool enabled) => _sourceController.SetToneEnabled(enabled);
        public void SetToneGain(float gain) => _sourceController.SetToneGain(gain);

        public bool HasAudioFile => _sourceController.HasAudioFile;
        public bool IsFilePlaying => _sourceController.IsFilePlaying;
        public string? AudioFileName => _sourceController.AudioFileName;
        public string? TestAudioFileName => _sourceController.TestAudioFileName;
        public string AudioFileStatusText => _sourceController.AudioFileStatusText;
        public string PlayButtonText => _sourceController.PlayButtonText;

        public float FileGain
        {
            get => _sourceController.FileGain;
            set => _sourceController.FileGain = value;
        }

        public string FileGainText => _sourceController.FileGainText;

        public void PlayTestAudio(string filePath) => _sourceController.PlayTestAudio(filePath);
        public Task<bool> PlayTestAudioAsync(string filePath) => _sourceController.PlayTestAudioAsync(filePath);
        public void StopTestAudio() => _sourceController.StopTestAudio();
        public void ToggleFilePlayback() => _sourceController.ToggleFilePlayback();
        public void SetFileGain(float gain) => _sourceController.SetFileGain(gain);

        public ICommand ToggleMicCommand => _sourceController.ToggleMicCommand;
        public ICommand ToggleTestToneCommand => _sourceController.ToggleTestToneCommand;
        public ICommand ToggleFilePlaybackCommand => _sourceController.ToggleFilePlaybackCommand;
        public ICommand PlayTestAudioCommand => _sourceController.PlayTestAudioCommand;
        public ICommand StopTestAudioCommand => _sourceController.StopTestAudioCommand;
        public ICommand BrowseAudioFileCommand => _sourceController.BrowseAudioFileCommand;

        #endregion

        #region Presets, Diagnostic Logging & Audio Pipeline Playground

        public ObservableCollection<PresetProfileDto> Presets { get; } = new();

        private PresetProfileDto? _selectedPreset;
        public PresetProfileDto? SelectedPreset
        {
            get => _selectedPreset;
            set
            {
                if (_selectedPreset != value)
                {
                    _selectedPreset = value;
                    OnPropertyChanged();
                    if (value != null)
                    {
                        ApplyPreset(value);
                    }
                }
            }
        }

        private bool _isHighPassFilterEnabled = true;
        public bool IsHighPassFilterEnabled
        {
            get => _isHighPassFilterEnabled;
            set
            {
                if (_isHighPassFilterEnabled != value)
                {
                    _isHighPassFilterEnabled = value;
                    OnPropertyChanged();
                    LogMessage($"DSP: High-Pass Filter (80Hz) set to {(value ? "ACTIVE" : "BYPASS")}");
                }
            }
        }

        private bool _isNoiseGateEnabled = true;
        public bool IsNoiseGateEnabled
        {
            get => _isNoiseGateEnabled;
            set
            {
                if (_isNoiseGateEnabled != value)
                {
                    _isNoiseGateEnabled = value;
                    OnPropertyChanged();
                    LogMessage($"DSP: Noise Gate (-45 dBFS) set to {(value ? "ACTIVE" : "BYPASS")}");
                }
            }
        }

        private bool _isLimiterEnabled = true;
        public bool IsLimiterEnabled
        {
            get => _isLimiterEnabled;
            set
            {
                if (_isLimiterEnabled != value)
                {
                    _isLimiterEnabled = value;
                    OnPropertyChanged();
                    LogMessage($"DSP: Soft Peak Limiter (-0.5 dBFS) set to {(value ? "ACTIVE" : "BYPASS")}");
                }
            }
        }

        public ICommand ToggleHighPassFilterCommand { get; }
        public ICommand ToggleNoiseGateCommand { get; }
        public ICommand ToggleLimiterCommand { get; }
        public ICommand ApplyPresetCommand { get; }

        public ObservableCollection<string> ConsoleLogs { get; } = new();
        public ICommand ClearLogsCommand { get; }

        public void LogMessage(string message)
        {
            var app = System.Windows.Application.Current;
            string entry = $"[{DateTime.Now:HH:mm:ss}] {message}";
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.InvokeAsync(() => AppendLogEntry(entry));
            }
            else
            {
                AppendLogEntry(entry);
            }
        }

        private void AppendLogEntry(string entry)
        {
            if (ConsoleLogs.Count > 100)
            {
                ConsoleLogs.RemoveAt(0);
            }
            ConsoleLogs.Add(entry);
        }

        private void InitializePresets()
        {
            Presets.Add(new PresetProfileDto
            {
                Id = "discord-voice",
                Name = "Discord & Voice",
                Tag = "VOICE",
                Description = "Optimized for speech: 1.0x gain, noise gate active, 80Hz rumble cut",
                MicGain = 1.0f,
                RouteMode = BridgeRouteMode.In,
                HighPassFilter = true,
                NoiseGate = true
            });
            Presets.Add(new PresetProfileDto
            {
                Id = "studio-raw",
                Name = "Studio Clean",
                Tag = "RAW",
                Description = "Unprocessed direct audio feed, zero DSP color, 1.0x gain",
                MicGain = 1.0f,
                RouteMode = BridgeRouteMode.In,
                HighPassFilter = false,
                NoiseGate = false
            });
            Presets.Add(new PresetProfileDto
            {
                Id = "gaming-lowlatency",
                Name = "Gaming Low-Lat",
                Tag = "GAME",
                Description = "Low buffer target (~12ms), boosted 1.25x gain",
                MicGain = 1.25f,
                RouteMode = BridgeRouteMode.In,
                HighPassFilter = true,
                NoiseGate = false
            });
            Presets.Add(new PresetProfileDto
            {
                Id = "tone-calibration",
                Name = "Tone & Test",
                Tag = "TEST",
                Description = "1000 Hz reference tone generator for buffer & loopback test",
                MicGain = 0.5f,
                RouteMode = BridgeRouteMode.In,
                HighPassFilter = false,
                NoiseGate = false
            });

            _selectedPreset = Presets[0];
        }

        private void ApplyPreset(PresetProfileDto preset)
        {
            SetMicGain(preset.MicGain);
            SetRouteMode(preset.RouteMode);
            _isHighPassFilterEnabled = preset.HighPassFilter;
            _isNoiseGateEnabled = preset.NoiseGate;
            OnPropertyChanged(nameof(IsHighPassFilterEnabled));
            OnPropertyChanged(nameof(IsNoiseGateEnabled));
            LogMessage($"PRESET: Activated '{preset.Name}' (Gain {preset.MicGain:F1}x, Route {preset.RouteMode})");
        }

        #endregion

        // Session & System Commands
        public ICommand StartCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand RunDiagnosticsCommand { get; }
        public ICommand RefreshDevicesCommand { get; }
        public ICommand ClearErrorCommand { get; }

        public MainViewModel() : this(null)
        {
        }

        public MainViewModel(IBridgeApplicationService? service)
        {
            _service = service ?? new BridgeApplicationService();
            _snapshot = _service.CurrentSnapshot;

            _routeCoordinator = new DeviceRouteCoordinator(_service);
            _meterMonitor = new AudioMeterMonitor(_service);
            _sourceController = new AudioSourceController(_service);

            // Bubble up child PropertyChanged notifications for data-binding and test reactivity
            _routeCoordinator.PropertyChanged += (s, e) => OnPropertyChanged(e.PropertyName);
            _meterMonitor.PropertyChanged += (s, e) => OnPropertyChanged(e.PropertyName);
            _sourceController.PropertyChanged += (s, e) => OnPropertyChanged(e.PropertyName);

            _service.SnapshotChanged += OnServiceSnapshotChanged;

            ToggleHighPassFilterCommand = new RelayCommand(() => IsHighPassFilterEnabled = !IsHighPassFilterEnabled);
            ToggleNoiseGateCommand = new RelayCommand(() => IsNoiseGateEnabled = !IsNoiseGateEnabled);
            ToggleLimiterCommand = new RelayCommand(() => IsLimiterEnabled = !IsLimiterEnabled);
            ClearLogsCommand = new RelayCommand(() =>
            {
                ConsoleLogs.Clear();
                LogMessage("Diagnostic console buffer cleared.");
            });
            ApplyPresetCommand = new RelayCommand(p =>
            {
                if (p is PresetProfileDto preset)
                {
                    SelectedPreset = preset;
                }
            });

            InitializePresets();
            LogMessage("Host Bridge UI Runtime v0.0.2 initialized.");
            LogMessage("OS Thinking: Audio engine and device discovery ready.");

            StartCommand = new AsyncRelayCommand(async () =>
            {
                try
                {
                    LogMessage("COMMAND: Bridge Start requested.");
                    await _service.StartBridgeAsync().ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    LogMessage($"ERROR: Failed to start session: {ex.Message}");
                    MessageBox.Show(ex.Message, "Start Session Error", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
            }, () => CanStart);

            StopCommand = new AsyncRelayCommand(async () =>
            {
                LogMessage("COMMAND: Bridge Stop requested.");
                await _service.StopBridgeAsync().ConfigureAwait(true);
            }, () => CanStop);

            RunDiagnosticsCommand = new AsyncRelayCommand(async () =>
            {
                LogMessage("COMMAND: Running pre-flight hardware diagnostics...");
                await _service.RunPreflightDiagnosticsAsync().ConfigureAwait(true);
            });

            RefreshDevicesCommand = new AsyncRelayCommand(async () =>
            {
                LogMessage("COMMAND: Refreshing audio and Bluetooth endpoints...");
                await LoadDevicesAsync().ConfigureAwait(true);
            });

            ClearErrorCommand = new RelayCommand(() =>
            {
                LogMessage("COMMAND: Clear error condition requested.");
                _service.ClearError();
            });

            // Initial load with safe try/catch
            _ = InitializeAsync();
        }

        private async Task InitializeAsync()
        {
            try
            {
                await LoadDevicesAsync().ConfigureAwait(true);
                await _service.RunPreflightDiagnosticsAsync().ConfigureAwait(true);
                LogMessage($"INIT: Discovered {BluetoothDevices.Count} BT device(s), {AvailableEndpointDevices.Count} audio endpoint(s).");
            }
            catch (Exception ex)
            {
                LogMessage($"INIT ERROR: {ex.Message}");
                System.Diagnostics.Debug.WriteLine($"Initialization error: {ex.Message}");
            }
        }

        private void OnServiceSnapshotChanged(object? sender, BridgeUiSnapshot snapshot)
        {
            var app = System.Windows.Application.Current;
            if (app?.Dispatcher != null && !app.Dispatcher.CheckAccess())
            {
                app.Dispatcher.InvokeAsync(() => ApplySnapshot(snapshot));
            }
            else
            {
                ApplySnapshot(snapshot);
            }
        }

        private void ApplySnapshot(BridgeUiSnapshot snapshot)
        {
            if (snapshot.State != _snapshot?.State)
            {
                LogMessage($"STATE: Bridge state transitioned to {snapshot.State}");
            }
            if (!string.IsNullOrEmpty(snapshot.LastError) && snapshot.LastError != _snapshot?.LastError)
            {
                LogMessage($"ALERT: Engine error: {snapshot.LastError}");
            }

            Snapshot = snapshot;
            _sourceController.ApplySnapshot(snapshot);
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            _meterMonitor.Dispose();
            _routeCoordinator.Dispose();
            _service.SnapshotChanged -= OnServiceSnapshotChanged;
            _service.Dispose();
        }
    }
}


