using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using PhoneMicBridge.Application;
using PhoneMicBridge.Core;
using PhoneMicBridge.Devices;

namespace PhoneMicBridge.Gui.ViewModels
{
    /// <summary>
    /// Coordinates audio route mode (IN host microphone vs OUT host speaker) and
    /// adaptive endpoint device filtering, synchronization, and safe asynchronous loading.
    /// Extracted from MainViewModel to isolate route/device lifecycle and invariants (Praise-OOP & OS-Thinking).
    /// </summary>
    public class DeviceRouteCoordinator : INotifyPropertyChanged, IDisposable
    {
        private readonly IBridgeApplicationService _service;
        private readonly SemaphoreSlim _deviceLoadLock = new(1, 1);

        private BridgeRouteMode _routeMode = BridgeRouteMode.In;
        private readonly ObservableCollection<AudioDeviceDto> _captureDevices = new();
        private readonly ObservableCollection<AudioDeviceDto> _renderDevices = new();
        private readonly ObservableCollection<BluetoothDeviceDto> _bluetoothDevices = new();
        private readonly ObservableCollection<AudioDeviceDto> _availableEndpointDevices = new();

        private AudioDeviceDto? _selectedCaptureDevice;
        private AudioDeviceDto? _selectedRenderDevice;
        private AudioDeviceDto? _selectedEndpointDevice;
        private BluetoothDeviceDto? _selectedBluetoothDevice;
        private bool _isBluetoothEnabled = true;
        private bool _isDisposed;

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<AudioDeviceDto> CaptureDevices => _captureDevices;
        public ObservableCollection<AudioDeviceDto> RenderDevices => _renderDevices;
        public ObservableCollection<BluetoothDeviceDto> BluetoothDevices => _bluetoothDevices;
        public ObservableCollection<AudioDeviceDto> AvailableEndpointDevices => _availableEndpointDevices;

        public BridgeRouteMode RouteMode
        {
            get => _routeMode;
            set => SetRouteMode(value);
        }

        public bool IsRouteIn => _routeMode == BridgeRouteMode.In;
        public bool IsRouteOut => _routeMode == BridgeRouteMode.Out;
        public string RouteModeText => _routeMode == BridgeRouteMode.In ? "IN (HOST MIC)" : "OUT (HOST SPK)";
        public string Dropdown2Label => _routeMode == BridgeRouteMode.In ? "HOST IN:" : "HOST OUT:";
        public string Dropdown2Placeholder => _routeMode == BridgeRouteMode.In ? "-- Select Input Microphone --" : "-- Select Output Speaker --";

        public AudioDeviceDto? SelectedCaptureDevice
        {
            get => _selectedCaptureDevice;
            set
            {
                if (_selectedCaptureDevice != value)
                {
                    _selectedCaptureDevice = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedCaptureDeviceId));
                    OnDeviceSelectionChanged();
                }
            }
        }

        public AudioDeviceDto? SelectedRenderDevice
        {
            get => _selectedRenderDevice;
            set
            {
                if (_selectedRenderDevice != value)
                {
                    _selectedRenderDevice = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedRenderDeviceId));
                    OnDeviceSelectionChanged();
                }
            }
        }

        public string? SelectedCaptureDeviceId => _selectedCaptureDevice?.Id;
        public string? SelectedRenderDeviceId => _selectedRenderDevice?.Id;

        public AudioDeviceDto? SelectedEndpointDevice
        {
            get => _selectedEndpointDevice;
            set
            {
                if (_selectedEndpointDevice != value)
                {
                    _selectedEndpointDevice = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedEndpointDeviceId));

                    if (_routeMode == BridgeRouteMode.In)
                    {
                        _selectedCaptureDevice = value;
                        OnPropertyChanged(nameof(SelectedCaptureDevice));
                        OnPropertyChanged(nameof(SelectedCaptureDeviceId));
                    }
                    else
                    {
                        _selectedRenderDevice = value;
                        OnPropertyChanged(nameof(SelectedRenderDevice));
                        OnPropertyChanged(nameof(SelectedRenderDeviceId));
                    }

                    OnDeviceSelectionChanged();
                }
            }
        }

        public string? SelectedEndpointDeviceId => _selectedEndpointDevice?.Id;

        public BluetoothDeviceDto? SelectedBluetoothDevice
        {
            get => _selectedBluetoothDevice;
            set
            {
                if (_selectedBluetoothDevice != value)
                {
                    // Gate 5: Selection Guard - reject binding offline or incompatible devices as active bridge target
                    if (value != null && !value.CanSelectForBridge)
                    {
                        return;
                    }

                    _selectedBluetoothDevice = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(SelectedBluetoothDeviceId));
                }
            }
        }

        public string? SelectedBluetoothDeviceId => _selectedBluetoothDevice?.Id;

        public bool IsBluetoothEnabled
        {
            get => _isBluetoothEnabled;
            private set
            {
                if (_isBluetoothEnabled != value)
                {
                    _isBluetoothEnabled = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(IsBluetoothDisabled));
                    OnPropertyChanged(nameof(BluetoothRadioText));
                    OnPropertyChanged(nameof(BluetoothButtonToolTip));
                }
            }
        }

        public bool IsBluetoothDisabled => !_isBluetoothEnabled;
        public bool HasBluetoothDevices => _bluetoothDevices.Count > 0;
        public bool IsBluetoothEmpty => _bluetoothDevices.Count == 0;
        public string BluetoothRadioText => _isBluetoothEnabled ? "BT ON" : "TURN ON";
        public string BluetoothButtonToolTip => _isBluetoothEnabled
            ? "Bluetooth Radio: ON (Click to toggle/turn OFF via WinRT API)"
            : "Bluetooth Radio: OFF (Click to TURN ON directly via WinRT API)";

        private bool _isOperatingBluetoothRadio;
        public bool IsOperatingBluetoothRadio
        {
            get => _isOperatingBluetoothRadio;
            private set
            {
                if (_isOperatingBluetoothRadio != value)
                {
                    _isOperatingBluetoothRadio = value;
                    OnPropertyChanged();
                }
            }
        }

        public ICommand SetRouteInCommand { get; }
        public ICommand SetRouteOutCommand { get; }
        public ICommand ToggleRouteModeCommand { get; }
        public ICommand OpenBluetoothSettingsCommand { get; }
        public ICommand TurnOnBluetoothCommand { get; }
        public ICommand ToggleBluetoothRadioCommand { get; }

        public DeviceRouteCoordinator(IBridgeApplicationService service)
        {
            _service = service ?? throw new ArgumentNullException(nameof(service));

            SetRouteInCommand = new RelayCommand(() => SetRouteMode(BridgeRouteMode.In));
            SetRouteOutCommand = new RelayCommand(() => SetRouteMode(BridgeRouteMode.Out));
            ToggleRouteModeCommand = new RelayCommand(() => SetRouteMode(_routeMode == BridgeRouteMode.In ? BridgeRouteMode.Out : BridgeRouteMode.In));
            OpenBluetoothSettingsCommand = new RelayCommand(() => _service.OpenBluetoothSettings());
            TurnOnBluetoothCommand = new RelayCommand(async () => await TurnOnBluetoothAsync(), () => !IsOperatingBluetoothRadio);
            ToggleBluetoothRadioCommand = new RelayCommand(async () => await ToggleBluetoothRadioAsync(), () => !IsOperatingBluetoothRadio);
        }

        public async Task<BluetoothRadioStateResult> TurnOnBluetoothAsync()
        {
            if (IsOperatingBluetoothRadio)
            {
                return BluetoothRadioStateResult.Failed;
            }

            try
            {
                IsOperatingBluetoothRadio = true;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();

                var result = await _service.TurnOnBluetoothRadioAsync().ConfigureAwait(true);
                if (result == BluetoothRadioStateResult.Success || result == BluetoothRadioStateResult.AlreadyInDesiredState)
                {
                    // Allow brief interval for Windows PnP audio endpoint enumeration to settle
                    await Task.Delay(300).ConfigureAwait(true);
                    await LoadDevicesAsync().ConfigureAwait(true);
                }
                else if (result == BluetoothRadioStateResult.DeniedByUser || result == BluetoothRadioStateResult.DeniedBySystem)
                {
                    _service.OpenBluetoothSettings();
                }
                return result;
            }
            finally
            {
                IsOperatingBluetoothRadio = false;
                System.Windows.Input.CommandManager.InvalidateRequerySuggested();
            }
        }

        public async Task<BluetoothRadioStateResult> ToggleBluetoothRadioAsync()
        {
            if (IsOperatingBluetoothRadio)
            {
                return BluetoothRadioStateResult.Failed;
            }

            try
            {
                IsOperatingBluetoothRadio = true;
                bool targetState = !_isBluetoothEnabled;
                var result = await _service.SetBluetoothRadioStateAsync(targetState).ConfigureAwait(true);
                if (result == BluetoothRadioStateResult.Success || result == BluetoothRadioStateResult.AlreadyInDesiredState)
                {
                    await LoadDevicesAsync().ConfigureAwait(true);
                }
                else if (result == BluetoothRadioStateResult.DeniedByUser || result == BluetoothRadioStateResult.DeniedBySystem)
                {
                    _service.OpenBluetoothSettings();
                }
                return result;
            }
            finally
            {
                IsOperatingBluetoothRadio = false;
            }
        }

        public void SetRouteMode(BridgeRouteMode mode)
        {
            if (_routeMode != mode)
            {
                _routeMode = mode;
                OnPropertyChanged(nameof(RouteMode));
                OnPropertyChanged(nameof(IsRouteIn));
                OnPropertyChanged(nameof(IsRouteOut));
                OnPropertyChanged(nameof(RouteModeText));
                OnPropertyChanged(nameof(Dropdown2Label));
                OnPropertyChanged(nameof(Dropdown2Placeholder));

                // Architectural Invariant: Default endpoint option must reset to null on mode switch to prevent route leaks
                SelectedEndpointDevice = null;

                UpdateAvailableEndpointDevices();
            }
        }

        public void UpdateAvailableEndpointDevices()
        {
            _availableEndpointDevices.Clear();
            var sourceList = _routeMode == BridgeRouteMode.In ? _captureDevices : _renderDevices;
            foreach (var d in sourceList)
            {
                // In OUT mode, only present devices that are currently Active to filter out phantom/unplugged drivers
                if (_routeMode == BridgeRouteMode.Out && !d.IsActive)
                {
                    continue;
                }

                _availableEndpointDevices.Add(d);
            }
        }

        public async Task LoadDevicesAsync()
        {
            await _deviceLoadLock.WaitAsync().ConfigureAwait(true);
            try
            {
                var snapshot = _service.CurrentSnapshot;
                string? prevCaptureId = _selectedCaptureDevice?.Id ?? snapshot.SelectedCaptureDeviceId;
                string? prevRenderId = _selectedRenderDevice?.Id ?? snapshot.SelectedRenderDeviceId;

                var captureList = await _service.GetCaptureDevicesAsync().ConfigureAwait(true);
                var renderList = await _service.GetRenderDevicesAsync().ConfigureAwait(true);
                var btList = await _service.GetBluetoothDevicesAsync().ConfigureAwait(true);

                _captureDevices.Clear();
                foreach (var d in captureList) if (d != null) _captureDevices.Add(d);

                _renderDevices.Clear();
                foreach (var d in renderList) if (d != null) _renderDevices.Add(d);

                _bluetoothDevices.Clear();
                foreach (var b in btList) if (b != null) _bluetoothDevices.Add(b);

                IsBluetoothEnabled = _service.IsBluetoothRadioEnabled();
                OnPropertyChanged(nameof(HasBluetoothDevices));
                OnPropertyChanged(nameof(IsBluetoothEmpty));

                if (_selectedBluetoothDevice == null || !_bluetoothDevices.Any(b => b != null && b.Id == _selectedBluetoothDevice.Id))
                {
                    _selectedBluetoothDevice = _bluetoothDevices.FirstOrDefault(b => b != null && b.IsConnected)
                                            ?? _bluetoothDevices.FirstOrDefault(b => b != null);
                    OnPropertyChanged(nameof(SelectedBluetoothDevice));
                    OnPropertyChanged(nameof(SelectedBluetoothDeviceId));
                }

                UpdateAvailableEndpointDevices();

                // Select capture device by stable ID if previous choice exists; otherwise default/active
                if (!string.IsNullOrEmpty(prevCaptureId))
                {
                    _selectedCaptureDevice = _captureDevices.FirstOrDefault(c => c != null && string.Equals(c.Id, prevCaptureId, StringComparison.OrdinalIgnoreCase));
                }
                if (_selectedCaptureDevice == null)
                {
                    _selectedCaptureDevice = _captureDevices.FirstOrDefault(c => c != null && c.IsDefault)
                                          ?? _captureDevices.FirstOrDefault(c => c != null && c.IsActive);
                }
                OnPropertyChanged(nameof(SelectedCaptureDevice));
                OnPropertyChanged(nameof(SelectedCaptureDeviceId));

                // Select render device by stable ID if previous choice exists; otherwise preferred HFP
                if (!string.IsNullOrEmpty(prevRenderId))
                {
                    _selectedRenderDevice = _renderDevices.FirstOrDefault(r => r != null && string.Equals(r.Id, prevRenderId, StringComparison.OrdinalIgnoreCase));
                }
                if (_selectedRenderDevice == null)
                {
                    _selectedRenderDevice = _renderDevices.FirstOrDefault(r => r != null && r.IsTargetHfp && r.IsActive)
                                         ?? _renderDevices.FirstOrDefault(r => r != null && r.IsTargetHfp)
                                         ?? _renderDevices.FirstOrDefault(r => r != null && r.IsActive);
                }
                OnPropertyChanged(nameof(SelectedRenderDevice));
                OnPropertyChanged(nameof(SelectedRenderDeviceId));

                // Default initial selection for Dropdown 2 if uninitialized
                if (_selectedEndpointDevice == null)
                {
                    _selectedEndpointDevice = _routeMode == BridgeRouteMode.In ? _selectedCaptureDevice : _selectedRenderDevice;
                    OnPropertyChanged(nameof(SelectedEndpointDevice));
                    OnPropertyChanged(nameof(SelectedEndpointDeviceId));
                }

                if (_selectedCaptureDevice != null || _selectedRenderDevice != null)
                {
                    await _service.SelectDevicesAsync(_selectedCaptureDevice?.Id, _selectedRenderDevice?.Id).ConfigureAwait(true);
                }
            }
            finally
            {
                _deviceLoadLock.Release();
            }
        }

        public void OnDeviceSelectionChanged()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await _service.SelectDevicesAsync(_selectedCaptureDevice?.Id, _selectedRenderDevice?.Id).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"Device selection error: {ex.Message}");
                }
            });
        }

        protected void OnPropertyChanged([CallerMemberName] string? name = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        public void Dispose()
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _deviceLoadLock.Dispose();
        }
    }
}
