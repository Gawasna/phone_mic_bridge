using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using PhoneMicBridge.Audio;
using PhoneMicBridge.Core;
using PhoneMicBridge.Devices;

namespace PhoneMicBridge.Application
{
    /// <summary>
    /// Thread-safe, idempotent implementation of IBridgeApplicationService.
    /// Bridges the Presentation Layer with Core audio engine, device discovery, and pre-flight diagnostics.
    /// </summary>
    public class BridgeApplicationService : IBridgeApplicationService
    {
        private readonly SemaphoreSlim _stateLock = new(1, 1);
        private readonly object _snapshotLock = new();
        private readonly IPreflightDiagnosticsEngine _diagnosticsEngine;
        private readonly DeviceNotificationWatcher _deviceWatcher;
        private readonly BridgeStateMachine _stateMachine;

        private WasapiAudioEngine? _audioEngine;
        private bool _isDisposed;

        // Device Selection
        private string? _selectedCaptureDeviceId;
        private string? _selectedCaptureDeviceName;
        private string? _selectedRenderDeviceId;
        private string? _selectedRenderDeviceName;

        // Audio Source Controls
        private bool _isMicEnabled = true;
        private float _micGain = 1.0f;
        private bool _hasAudioFile;
        private bool _isFilePlaying;
        private string? _audioFilePath;
        private string? _audioFileName;
        private float _fileGain = 0.7f;
        private bool _isToneEnabled;
        private double _toneFrequency = 1000.0;
        private float _toneGain = 0.35f;

        // Preflight & Error state
        private DiagnosticOverallStatus _preflightOverall = DiagnosticOverallStatus.Unknown;
        private IReadOnlyList<PreflightItemDto> _preflightItems = Array.Empty<PreflightItemDto>();
        private string? _lastError;
        private readonly IBluetoothRadioController _radioController;

        // Current immutable snapshot
        private BridgeUiSnapshot _currentSnapshot;

        public BridgeUiSnapshot CurrentSnapshot
        {
            get
            {
                lock (_snapshotLock)
                {
                    return _currentSnapshot;
                }
            }
        }

        public event EventHandler<BridgeUiSnapshot>? SnapshotChanged;

        public BridgeApplicationService(IPreflightDiagnosticsEngine? diagnosticsEngine = null)
            : this(diagnosticsEngine, null)
        {
        }

        public BridgeApplicationService(
            IPreflightDiagnosticsEngine? diagnosticsEngine,
            IBluetoothRadioController? radioController)
        {
            _diagnosticsEngine = diagnosticsEngine ?? new PreflightDiagnosticsEngine();
            _radioController = radioController ?? WinRtBluetoothRadioController.Instance;
            _stateMachine = new BridgeStateMachine(BridgeState.Disconnected);
            _stateMachine.StateChanged += OnStateMachineStateChanged;

            _deviceWatcher = new DeviceNotificationWatcher();
            _deviceWatcher.DeviceStateChanged += OnDeviceStateChanged;
            _deviceWatcher.DeviceAdded += OnDevicesListMutated;
            _deviceWatcher.DeviceRemoved += OnDevicesListMutated;

            _radioController.RadioStateChanged += OnRadioStateChanged;

            _currentSnapshot = BridgeUiSnapshot.Initial();
            PublishSnapshot();
        }

        public AudioMeterValues GetCurrentMeterValues()
        {
            var engine = _audioEngine;
            if (engine != null && _stateMachine.CurrentState == BridgeState.Streaming)
            {
                float inPeak = engine.MicMeter.Peak;
                float inRms = engine.MicMeter.Rms;
                float inPeakDb = AmplitudeToDb(inPeak);
                bool inClip = inPeakDb >= -0.1f || inPeak >= 0.98855f;

                float outPeak = engine.OutputMeter.Peak;
                float outRms = engine.OutputMeter.Rms;
                float outPeakDb = AmplitudeToDb(outPeak);
                bool outClip = outPeakDb >= -0.1f || outPeak >= 0.98855f;

                return new AudioMeterValues(inPeak, inRms, inPeakDb, outPeak, outRms, outPeakDb, inClip, outClip);
            }

            return new AudioMeterValues(0f, 0f, -60.0f, 0f, 0f, -60.0f, false, false);
        }

        public async Task<BridgeUiSnapshot> RunPreflightDiagnosticsAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            _preflightOverall = DiagnosticOverallStatus.InProgress;
            PublishSnapshot();

            try
            {
                var items = await _diagnosticsEngine.ExecuteChecksAsync(
                    _selectedCaptureDeviceId,
                    _selectedRenderDeviceId,
                    cancellationToken
                ).ConfigureAwait(false);

                _preflightItems = items;

                if (items.Any(i => string.Equals(i.Status, "Fail", StringComparison.OrdinalIgnoreCase)))
                {
                    _preflightOverall = DiagnosticOverallStatus.Fail;
                }
                else if (items.Any(i => string.Equals(i.Status, "Unknown", StringComparison.OrdinalIgnoreCase)))
                {
                    _preflightOverall = DiagnosticOverallStatus.Unknown;
                }
                else
                {
                    _preflightOverall = DiagnosticOverallStatus.Pass;
                }
            }
            catch (OperationCanceledException)
            {
                _preflightOverall = DiagnosticOverallStatus.Unknown;
                throw;
            }
            catch (Exception ex)
            {
                _preflightOverall = DiagnosticOverallStatus.Fail;
                _lastError = $"Diagnostics error: {ex.Message}";
            }

            PublishSnapshot();
            return CurrentSnapshot;
        }

        public async Task<IReadOnlyList<AudioDeviceDto>> GetCaptureDevicesAsync()
        {
            return await Task.Run<IReadOnlyList<AudioDeviceDto>>(() =>
            {
                string? defaultId = null;
                try
                {
                    var def = DeviceDiscovery.GetDefaultCaptureDevice();
                    defaultId = def.ID;
                }
                catch
                {
                    // No default capture endpoint available
                }

                var rawList = DeviceDiscovery.GetCaptureDevices();
                var results = new List<AudioDeviceDto>(rawList.Count);
                foreach (var d in rawList)
                {
                    bool isDefault = defaultId != null && string.Equals(d.Id, defaultId, StringComparison.OrdinalIgnoreCase);
                    results.Add(new AudioDeviceDto(
                        Id: d.Id,
                        Name: d.Name,
                        IsActive: d.State == DeviceState.Active,
                        IsTargetHfp: false,
                        IsDefault: isDefault
                    ));
                }
                return results;
            }).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<AudioDeviceDto>> GetRenderDevicesAsync()
        {
            return await Task.Run<IReadOnlyList<AudioDeviceDto>>(() =>
            {
                var rawList = DeviceDiscovery.GetRenderDevices();
                var results = new List<AudioDeviceDto>(rawList.Count);
                foreach (var d in rawList)
                {
                    results.Add(new AudioDeviceDto(
                        Id: d.Id,
                        Name: d.Name,
                        IsActive: d.State == DeviceState.Active,
                        IsTargetHfp: d.IsTargetHfp,
                        IsDefault: false
                    ));
                }
                return results;
            }).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<BluetoothDeviceDto>> GetBluetoothDevicesAsync()
        {
            return await Task.Run<IReadOnlyList<BluetoothDeviceDto>>(() =>
            {
                return BluetoothDeviceDiscovery.DiscoverBluetoothDevices();
            }).ConfigureAwait(false);
        }

        public bool IsBluetoothRadioEnabled()
        {
            return _radioController.IsRadioEnabled();
        }

        public async Task<BluetoothRadioStateResult> TurnOnBluetoothRadioAsync()
        {
            return await SetBluetoothRadioStateAsync(true).ConfigureAwait(false);
        }

        public async Task<BluetoothRadioStateResult> SetBluetoothRadioStateAsync(bool enable)
        {
            var result = await _radioController.SetRadioStateAsync(enable).ConfigureAwait(false);
            if (result == BluetoothRadioStateResult.Success || result == BluetoothRadioStateResult.AlreadyInDesiredState)
            {
                await RunPreflightDiagnosticsAsync().ConfigureAwait(false);
            }
            return result;
        }

        public void OpenBluetoothSettings()
        {
            _radioController.OpenWindowsBluetoothSettings();
        }

        public bool OpenWindowsPrivacySettings()
        {
            return _radioController.OpenWindowsPrivacySettings();
        }

        public async Task SelectDevicesAsync(string? captureDeviceId, string? renderDeviceId)
        {
            _selectedCaptureDeviceId = captureDeviceId;
            _selectedRenderDeviceId = renderDeviceId;

            // Lookup friendly names
            var captureDevices = await GetCaptureDevicesAsync().ConfigureAwait(false);
            var selectedCapture = captureDevices.FirstOrDefault(c => c.Id == captureDeviceId);
            _selectedCaptureDeviceName = selectedCapture?.Name;

            var renderDevices = await GetRenderDevicesAsync().ConfigureAwait(false);
            var selectedRender = renderDevices.FirstOrDefault(r => r.Id == renderDeviceId);
            _selectedRenderDeviceName = selectedRender?.Name;

            await RunPreflightDiagnosticsAsync().ConfigureAwait(false);
        }

        public async Task StartBridgeAsync(CancellationToken cancellationToken = default)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);

            await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Idempotent: If already streaming, no-op
                if (_stateMachine.CurrentState == BridgeState.Streaming)
                {
                    return;
                }

                // Safety Interlock Gating
                if (_preflightOverall == DiagnosticOverallStatus.Fail)
                {
                    throw new InvalidOperationException("Cannot start audio session: Pre-flight diagnostics failed. Please resolve reported issues.");
                }

                if (_preflightOverall == DiagnosticOverallStatus.InProgress)
                {
                    throw new InvalidOperationException("Cannot start audio session: Pre-flight diagnostics are in progress. Please wait for checks to complete.");
                }

                _stateMachine.TransitionTo(BridgeState.Connecting);
                _lastError = null;
                PublishSnapshot();

                cancellationToken.ThrowIfCancellationRequested();

                // 1. Resolve Target Render Device
                MMDevice? targetRender = null;
                using var enumerator = new MMDeviceEnumerator();

                if (!string.IsNullOrWhiteSpace(_selectedRenderDeviceId))
                {
                    try
                    {
                        targetRender = enumerator.GetDevice(_selectedRenderDeviceId);
                    }
                    catch
                    {
                        // Fall back to auto-discovery
                    }
                }

                if (targetRender == null)
                {
                    targetRender = DeviceDiscovery.FindTargetRenderDevice();
                }

                if (targetRender == null || targetRender.State != DeviceState.Active)
                {
                    throw new InvalidOperationException("Target Bluetooth Hands-Free audio output device could not be found or is not active.");
                }

                // Sync auto-discovered render device ID & name (Addresses reviewer_m1_1 finding)
                _selectedRenderDeviceId ??= targetRender.ID;
                _selectedRenderDeviceName ??= targetRender.FriendlyName;

                // 2. Resolve Microphone Capture Device
                MMDevice? captureDevice = null;
                if (!string.IsNullOrWhiteSpace(_selectedCaptureDeviceId))
                {
                    try
                    {
                        captureDevice = enumerator.GetDevice(_selectedCaptureDeviceId);
                    }
                    catch
                    {
                        // Fall back to default
                    }
                }

                if (captureDevice == null)
                {
                    try
                    {
                        captureDevice = DeviceDiscovery.GetDefaultCaptureDevice();
                    }
                    catch
                    {
                        // Mic capture device optional if tone or file is used
                    }
                }

                // Sync auto-discovered capture device ID & name
                if (captureDevice != null)
                {
                    _selectedCaptureDeviceId ??= captureDevice.ID;
                    _selectedCaptureDeviceName ??= captureDevice.FriendlyName;
                }

                cancellationToken.ThrowIfCancellationRequested();

                // 3. Initialize Audio Engine & Pipeline
                var engine = new WasapiAudioEngine(targetRender, captureDevice);
                engine.Pipeline.SetMicGain(_micGain);
                engine.Pipeline.SetFileGain(_fileGain);
                engine.Pipeline.SetToneGain(_toneGain);

                if (_hasAudioFile && !string.IsNullOrWhiteSpace(_audioFilePath))
                {
                    engine.Pipeline.AttachFileSource(_audioFilePath, _fileGain);
                }

                if (_isToneEnabled)
                {
                    engine.Pipeline.SetToneEnabled(true, _toneFrequency, _toneGain);
                }

                if (captureDevice != null)
                {
                    engine.SetupMicCapture(_isMicEnabled);
                }

                engine.StateChanged += OnAudioEngineStateChanged;
                engine.DeviceWarningReceived += OnAudioEngineWarning;

                cancellationToken.ThrowIfCancellationRequested();

                engine.Start();
                _audioEngine = engine;

                _stateMachine.TransitionTo(BridgeState.Streaming);
                _lastError = null;
                PublishSnapshot();
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (_audioEngine != null)
                    {
                        _audioEngine.StateChanged -= OnAudioEngineStateChanged;
                        _audioEngine.DeviceWarningReceived -= OnAudioEngineWarning;
                        _audioEngine.Stop();
                        _audioEngine.Dispose();
                        _audioEngine = null;
                    }
                }
                catch
                {
                    // Suppress disposal errors on cancellation
                }

                if (_stateMachine.CurrentState == BridgeState.Connecting)
                {
                    _stateMachine.TryTransitionTo(BridgeState.Stopped);
                }
                PublishSnapshot();
                throw;
            }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                try
                {
                    if (_audioEngine != null)
                    {
                        _audioEngine.StateChanged -= OnAudioEngineStateChanged;
                        _audioEngine.DeviceWarningReceived -= OnAudioEngineWarning;
                        _audioEngine.Stop();
                        _audioEngine.Dispose();
                        _audioEngine = null;
                    }
                }
                catch
                {
                }
                _stateMachine.TryTransitionTo(BridgeState.Error);
                PublishSnapshot();
                throw;
            }
            finally
            {
                _stateLock.Release();
            }
        }

        public async Task StopBridgeAsync(CancellationToken cancellationToken = default)
        {
            if (_isDisposed) return;

            await _stateLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Idempotent: If already stopped or disconnected, no-op
                if (_stateMachine.CurrentState == BridgeState.Stopped || _stateMachine.CurrentState == BridgeState.Disconnected)
                {
                    return;
                }

                if (_audioEngine != null)
                {
                    _audioEngine.StateChanged -= OnAudioEngineStateChanged;
                    _audioEngine.DeviceWarningReceived -= OnAudioEngineWarning;
                    try
                    {
                        _audioEngine.Stop();
                    }
                    catch
                    {
                    }
                    try
                    {
                        _audioEngine.Dispose();
                    }
                    catch
                    {
                    }
                    _audioEngine = null;
                }

                _stateMachine.TryTransitionTo(BridgeState.Stopped);
                PublishSnapshot();
            }
            finally
            {
                _stateLock.Release();
            }
        }

        public void SetMicEnabled(bool enabled)
        {
            _isMicEnabled = enabled;
            _audioEngine?.Pipeline.SetMicEnabled(enabled);
            PublishSnapshot();
        }

        public void SetMicMuted(bool muted)
        {
            SetMicEnabled(!muted);
        }

        public void ToggleMic()
        {
            SetMicEnabled(!_isMicEnabled);
        }

        public void SetMicGain(float gain)
        {
            _micGain = Math.Clamp(gain, 0.0f, 2.0f);
            _audioEngine?.Pipeline.SetMicGain(_micGain);
            PublishSnapshot();
        }

        public Task<bool> LoadAudioFileAsync(string filePath, float initialGain = 0.7f)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return Task.FromResult(false);
            }

            _audioFilePath = filePath;
            _audioFileName = Path.GetFileName(filePath);
            _fileGain = Math.Clamp(initialGain, 0.0f, 2.0f);
            _hasAudioFile = true;
            _isFilePlaying = true;

            if (_audioEngine != null)
            {
                _audioEngine.Pipeline.AttachFileSource(filePath, _fileGain);
            }

            PublishSnapshot();
            return Task.FromResult(true);
        }

        public void PlayTestAudio(string filePath)
        {
            LoadAudioFileAsync(filePath).GetAwaiter().GetResult();
        }

        public async Task<bool> PlayTestAudioAsync(string filePath)
        {
            return await LoadAudioFileAsync(filePath).ConfigureAwait(false);
        }

        public void StopTestAudio()
        {
            if (_audioEngine != null)
            {
                if (_audioEngine.Pipeline.IsFilePlaying)
                {
                    _audioEngine.Pipeline.ToggleFile();
                }
                _isFilePlaying = _audioEngine.Pipeline.IsFilePlaying;
            }
            else
            {
                _isFilePlaying = false;
            }

            PublishSnapshot();
        }

        public void ToggleFilePlayback()
        {
            if (!_hasAudioFile) return;

            if (_audioEngine != null)
            {
                _audioEngine.Pipeline.ToggleFile();
                _isFilePlaying = _audioEngine.Pipeline.IsFilePlaying;
            }
            else
            {
                _isFilePlaying = !_isFilePlaying;
            }

            PublishSnapshot();
        }

        public void SetFileGain(float gain)
        {
            _fileGain = Math.Clamp(gain, 0.0f, 2.0f);
            _audioEngine?.Pipeline.SetFileGain(_fileGain);
            PublishSnapshot();
        }

        public void SetToneEnabled(bool enabled, double frequency = 1000.0, float initialGain = 0.35f)
        {
            _isToneEnabled = enabled;
            _toneFrequency = frequency;
            _toneGain = Math.Clamp(initialGain, 0.0f, 2.0f);

            _audioEngine?.Pipeline.SetToneEnabled(enabled, frequency, _toneGain);
            PublishSnapshot();
        }

        public void ToggleTestTone()
        {
            SetToneEnabled(!_isToneEnabled, _toneFrequency, _toneGain);
        }

        public void SetToneGain(float gain)
        {
            _toneGain = Math.Clamp(gain, 0.0f, 2.0f);
            _audioEngine?.Pipeline.SetToneGain(_toneGain);
            PublishSnapshot();
        }

        public void ClearError()
        {
            _lastError = null;
            if (_stateMachine.CurrentState == BridgeState.Error)
            {
                _stateMachine.TryTransitionTo(BridgeState.Disconnected);
            }
            if (_preflightOverall == DiagnosticOverallStatus.InProgress && _preflightItems.Count > 0 && !_preflightItems.Any(i => string.Equals(i.Status, "Fail", StringComparison.OrdinalIgnoreCase)))
            {
                _preflightOverall = DiagnosticOverallStatus.Pass;
            }
            PublishSnapshot();
        }

        private void OnStateMachineStateChanged(object? sender, BridgeState state)
        {
            PublishSnapshot();
        }

        private void HandleSuddenDisconnection(string errorMessage = "Target HFP device disconnected")
        {
            try
            {
                if (_audioEngine != null)
                {
                    _audioEngine.StateChanged -= OnAudioEngineStateChanged;
                    _audioEngine.DeviceWarningReceived -= OnAudioEngineWarning;
                    try
                    {
                        _audioEngine.Stop();
                    }
                    catch
                    {
                    }
                    try
                    {
                        _audioEngine.Dispose();
                    }
                    catch
                    {
                    }
                    _audioEngine = null;
                }
            }
            catch
            {
                // Suppress COM/WASAPI exceptions on sudden hardware disconnection
            }

            _lastError = errorMessage;
            _stateMachine.TryTransitionTo(BridgeState.Error);
            PublishSnapshot();
        }

        private void OnAudioEngineStateChanged(object? sender, BridgeState state)
        {
            if (state == BridgeState.Error)
            {
                _lastError ??= "Target HFP device disconnected";
                _stateMachine.TryTransitionTo(BridgeState.Error);
            }
            PublishSnapshot();
        }

        private void OnAudioEngineWarning(object? sender, string warning)
        {
            if (warning.Contains("Target HFP device state changed", StringComparison.OrdinalIgnoreCase))
            {
                HandleSuddenDisconnection("Target HFP device disconnected");
            }
            else
            {
                _lastError = warning;
                PublishSnapshot();
            }
        }

        private void OnDeviceStateChanged(object? sender, DeviceStateChangedEventArgs e)
        {
            bool isRenderDevice = (!string.IsNullOrWhiteSpace(_selectedRenderDeviceId) && string.Equals(e.DeviceId, _selectedRenderDeviceId, StringComparison.OrdinalIgnoreCase))
                || (_audioEngine != null && string.Equals(e.DeviceId, _audioEngine.RenderDevice.ID, StringComparison.OrdinalIgnoreCase));

            bool isCaptureDevice = (!string.IsNullOrWhiteSpace(_selectedCaptureDeviceId) && string.Equals(e.DeviceId, _selectedCaptureDeviceId, StringComparison.OrdinalIgnoreCase))
                || (_audioEngine?.CaptureDevice != null && string.Equals(e.DeviceId, _audioEngine.CaptureDevice.ID, StringComparison.OrdinalIgnoreCase));

            if ((isRenderDevice || isCaptureDevice) && e.NewState != DeviceState.Active)
            {
                if (_stateMachine.CurrentState == BridgeState.Streaming)
                {
                    HandleSuddenDisconnection("Target HFP device disconnected");
                }
            }

            // Auto-refresh diagnostics in background
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunPreflightDiagnosticsAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Ignore background scan errors
                }
            });
        }

        private void OnDevicesListMutated(object? sender, DeviceNotificationEventArgs e)
        {
            bool isRenderDevice = (!string.IsNullOrWhiteSpace(_selectedRenderDeviceId) && string.Equals(e.DeviceId, _selectedRenderDeviceId, StringComparison.OrdinalIgnoreCase))
                || (_audioEngine != null && string.Equals(e.DeviceId, _audioEngine.RenderDevice.ID, StringComparison.OrdinalIgnoreCase));

            bool isCaptureDevice = (!string.IsNullOrWhiteSpace(_selectedCaptureDeviceId) && string.Equals(e.DeviceId, _selectedCaptureDeviceId, StringComparison.OrdinalIgnoreCase))
                || (_audioEngine?.CaptureDevice != null && string.Equals(e.DeviceId, _audioEngine.CaptureDevice.ID, StringComparison.OrdinalIgnoreCase));

            if ((isRenderDevice || isCaptureDevice) && _stateMachine.CurrentState == BridgeState.Streaming)
            {
                HandleSuddenDisconnection("Target HFP device disconnected");
            }

            _ = Task.Run(async () =>
            {
                try
                {
                    await RunPreflightDiagnosticsAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Ignore background scan errors
                }
            });
        }

        private void OnRadioStateChanged(object? sender, bool isEnabled)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RunPreflightDiagnosticsAsync().ConfigureAwait(false);
                }
                catch
                {
                    // Ignore background scan errors
                }
            });
        }

        private void PublishSnapshot()
        {
            lock (_snapshotLock)
            {
                var state = _stateMachine.CurrentState;

                // Truthful status separation
                var renderStatus = state == BridgeState.Streaming
                    ? EndpointRenderStatus.Active
                    : (state == BridgeState.Error ? EndpointRenderStatus.Error : EndpointRenderStatus.Inactive);

                var btStatus = state == BridgeState.Streaming
                    ? BluetoothTransportStatus.ScoActive
                    : (state == BridgeState.Connecting ? BluetoothTransportStatus.Connecting : BluetoothTransportStatus.Disconnected);

                // Truthful display: Delivery status remains Unconfirmed without bidirectional companion ACK
                var deliveryStatus = DeliveryAckStatus.Unconfirmed;

                string stateDesc = state switch
                {
                    BridgeState.Disconnected => "Disconnected - Ready to connect",
                    BridgeState.Connecting => "Connecting - Initializing WASAPI audio endpoint...",
                    BridgeState.Streaming => "Streaming - Audio transmission active",
                    BridgeState.Paused => "Paused",
                    BridgeState.Stopped => "Stopped - Session completed",
                    BridgeState.Error => $"Error - {_lastError ?? "Unknown audio pipeline failure"}",
                    _ => state.ToString()
                };

                bool canStart = (state == BridgeState.Disconnected || state == BridgeState.Stopped || state == BridgeState.Error)
                             && _preflightOverall != DiagnosticOverallStatus.Fail
                             && _preflightOverall != DiagnosticOverallStatus.InProgress;

                double fileCur = _audioEngine?.Pipeline.FileCurrentSeconds ?? 0;
                double fileTot = _audioEngine?.Pipeline.FileTotalSeconds ?? 0;

                _currentSnapshot = new BridgeUiSnapshot(
                    Timestamp: DateTimeOffset.UtcNow,
                    State: state,
                    StateDescription: stateDesc,
                    LastError: _lastError,
                    RenderStatus: renderStatus,
                    BluetoothStatus: btStatus,
                    DeliveryStatus: deliveryStatus,
                    SelectedCaptureDeviceId: _selectedCaptureDeviceId,
                    SelectedCaptureDeviceName: _selectedCaptureDeviceName,
                    SelectedRenderDeviceId: _selectedRenderDeviceId,
                    SelectedRenderDeviceName: _selectedRenderDeviceName,
                    IsMicEnabled: _isMicEnabled,
                    MicGain: _micGain,
                    HasAudioFile: _hasAudioFile,
                    IsFilePlaying: _isFilePlaying,
                    AudioFileName: _audioFileName,
                    FileGain: _fileGain,
                    FileCurrentSeconds: fileCur,
                    FileTotalSeconds: fileTot,
                    IsToneEnabled: _isToneEnabled,
                    ToneFrequency: _toneFrequency,
                    ToneGain: _toneGain,
                    PreflightOverall: _preflightOverall,
                    CanStartSession: canStart,
                    PreflightItems: _preflightItems
                );
            }

            SnapshotChanged?.Invoke(this, _currentSnapshot);
        }

        private static float AmplitudeToDb(float amplitude, float minDb = -60.0f)
        {
            if (amplitude <= 0.0001f) return minDb;
            float db = 20.0f * MathF.Log10(amplitude);
            return db < minDb ? minDb : db;
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;

            try
            {
                _radioController.RadioStateChanged -= OnRadioStateChanged;
                _stateMachine.StateChanged -= OnStateMachineStateChanged;
                _deviceWatcher.DeviceStateChanged -= OnDeviceStateChanged;
                _deviceWatcher.DeviceAdded -= OnDevicesListMutated;
                _deviceWatcher.DeviceRemoved -= OnDevicesListMutated;
                _deviceWatcher.Dispose();

                if (_audioEngine != null)
                {
                    _audioEngine.StateChanged -= OnAudioEngineStateChanged;
                    _audioEngine.DeviceWarningReceived -= OnAudioEngineWarning;
                    _audioEngine.Stop();
                    _audioEngine.Dispose();
                    _audioEngine = null;
                }
            }
            catch
            {
                // Suppress shutdown races
            }
            finally
            {
                _stateLock.Dispose();
            }
        }
    }
}
