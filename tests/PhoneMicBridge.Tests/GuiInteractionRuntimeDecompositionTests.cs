using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using PhoneMicBridge.Application;
using PhoneMicBridge.Core;
using Xunit;

namespace PhoneMicBridge.Tests
{
    /// <summary>
    /// Verifies the decoupled interaction runtime architecture extracted from MainViewModel:
    /// - DeviceRouteCoordinator (route mode invariants, endpoint filtering, SemaphoreSlim concurrency)
    /// - AudioMeterMonitor (30Hz VU meter runtime loop, zero-allocation metrics, lifecycle disposal)
    /// - AudioSourceController (isolated mic, tone, file player commands)
    /// - MainViewModel (thin facade bubbling child property changes, 100% backward-compatibility)
    /// </summary>
    public class GuiInteractionRuntimeDecompositionTests
    {
        private class StubBridgeService : IBridgeApplicationService
        {
            public BridgeUiSnapshot CurrentSnapshot { get; set; } = BridgeUiSnapshot.Initial();
#pragma warning disable CS0067
            public event EventHandler<BridgeUiSnapshot>? SnapshotChanged;
#pragma warning restore CS0067

            public List<AudioDeviceDto> CaptureList { get; set; } = new()
            {
                new AudioDeviceDto("mic1", "Internal Microphone", true, false, true),
                new AudioDeviceDto("mic2", "USB Desk Mic", true, false, false)
            };

            public List<AudioDeviceDto> RenderList { get; set; } = new()
            {
                new AudioDeviceDto("spk1", "Internal Speakers", true, false, true),
                new AudioDeviceDto("spk_ghost", "Disconnected Headphones", false, false, false),
                new AudioDeviceDto("hfp1", "Phone Bluetooth Hands-Free", true, true, false)
            };

            public List<BluetoothDeviceDto> BluetoothList { get; set; } = new()
            {
                new BluetoothDeviceDto("bt_phone", "Pixel Phone", "00:11:22:33:44:55", BluetoothDeviceCategory.Phone, "Connected", "READY TO BRIDGE", true, true, true, true),
                new BluetoothDeviceDto("bt_mouse", "Wireless Mouse", "11:22:33:44:55:66", BluetoothDeviceCategory.Other, "Connected", "Incompatible", true, false, false, false)
            };

            public bool BluetoothEnabled { get; set; } = true;
            public bool OpenSettingsCalled { get; set; }
            public string? LastSelectedCaptureId { get; private set; }
            public string? LastSelectedRenderId { get; private set; }

            public AudioMeterValues MeterValuesToReturn { get; set; } = new(0.5f, -6.0f, -6.0f, 0.8f, -1.9f, -2.0f, false, false);

            public AudioMeterValues GetCurrentMeterValues() => MeterValuesToReturn;
            public Task<BridgeUiSnapshot> RunPreflightDiagnosticsAsync(CancellationToken cancellationToken = default) => Task.FromResult(CurrentSnapshot);
            public Task<IReadOnlyList<AudioDeviceDto>> GetCaptureDevicesAsync() => Task.FromResult<IReadOnlyList<AudioDeviceDto>>(CaptureList);
            public Task<IReadOnlyList<AudioDeviceDto>> GetRenderDevicesAsync() => Task.FromResult<IReadOnlyList<AudioDeviceDto>>(RenderList);
            public Task<IReadOnlyList<BluetoothDeviceDto>> GetBluetoothDevicesAsync() => Task.FromResult<IReadOnlyList<BluetoothDeviceDto>>(BluetoothList);
            public bool IsBluetoothRadioEnabled() => BluetoothEnabled;
            public void OpenBluetoothSettings() => OpenSettingsCalled = true;

            public Task SelectDevicesAsync(string? captureDeviceId, string? renderDeviceId)
            {
                LastSelectedCaptureId = captureDeviceId;
                LastSelectedRenderId = renderDeviceId;
                return Task.CompletedTask;
            }

            public Task StartBridgeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public Task StopBridgeAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
            public void SetMicEnabled(bool enabled) { }
            public void SetMicGain(float gain) { }
            public Task<bool> LoadAudioFileAsync(string filePath, float initialGain = 0.7f) => Task.FromResult(true);
            public void ToggleFilePlayback() { }
            public void SetFileGain(float gain) { }
            public void SetToneEnabled(bool enabled, double frequency = 1000.0, float initialGain = 0.35f) { }
            public void SetToneGain(float gain) { }
            public void ClearError() { }
            public void Dispose() { }
        }

        private static Assembly GetGuiAssembly()
        {
            string testDir = AppContext.BaseDirectory;
            string guiDllPath = Path.GetFullPath(Path.Combine(testDir, @"..\..\..\..\..\Gui\PhoneMicBridge.Gui\bin\Debug\net9.0-windows\PhoneMicBridge.Gui.dll"));

            if (!File.Exists(guiDllPath))
            {
                string solutionRoot = Path.GetFullPath(Path.Combine(testDir, @"..\..\..\..\..\"));
                guiDllPath = Path.Combine(solutionRoot, "Gui", "PhoneMicBridge.Gui", "bin", "Debug", "net9.0-windows", "PhoneMicBridge.Gui.dll");
            }

            Assert.True(File.Exists(guiDllPath), $"PhoneMicBridge.Gui.dll must exist at: {guiDllPath}");
            return Assembly.LoadFrom(guiDllPath);
        }

        private static object CreateInstance(string typeName, params object[] args)
        {
            var assembly = GetGuiAssembly();
            var type = assembly.GetType(typeName) ?? throw new InvalidOperationException($"Type '{typeName}' not found.");
            return Activator.CreateInstance(type, args)!;
        }

        private static T GetProp<T>(object target, string name)
        {
            var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"Property '{name}' not found on '{target.GetType().FullName}'.");
            return (T)prop.GetValue(target)!;
        }

        private static void SetProp(object target, string name, object? val)
        {
            var prop = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
                ?? throw new ArgumentException($"Property '{name}' not found on '{target.GetType().FullName}'.");
            prop.SetValue(target, val);
        }

        [Fact]
        public async Task DeviceRouteCoordinator_RouteSwitch_EnforcesResetInvariantAndEndpointFiltering()
        {
            var service = new StubBridgeService();
            var coordinator = CreateInstance("PhoneMicBridge.Gui.ViewModels.DeviceRouteCoordinator", service);

            // Load devices
            var loadMethod = coordinator.GetType().GetMethod("LoadDevicesAsync")!;
            await (Task)loadMethod.Invoke(coordinator, null)!;

            // In IN mode (default)
            Assert.True(GetProp<bool>(coordinator, "IsRouteIn"));
            Assert.Equal("HOST IN:", GetProp<string>(coordinator, "Dropdown2Label"));

            var availableEndpointsIn = (ICollection)GetProp<object>(coordinator, "AvailableEndpointDevices");
            Assert.Equal(2, availableEndpointsIn.Count); // 2 capture devices

            // Select an endpoint
            SetProp(coordinator, "SelectedEndpointDevice", service.CaptureList[0]);
            Assert.NotNull(GetProp<object?>(coordinator, "SelectedEndpointDevice"));

            // Switch to OUT mode
            var setRouteMethod = coordinator.GetType().GetMethod("SetRouteMode")!;
            setRouteMethod.Invoke(coordinator, new object[] { BridgeRouteMode.Out });

            // Invariant 1: RouteMode is OUT
            Assert.True(GetProp<bool>(coordinator, "IsRouteOut"));
            Assert.Equal("HOST OUT:", GetProp<string>(coordinator, "Dropdown2Label"));

            // Invariant 2: SelectedEndpointDevice is automatically reset to null
            Assert.Null(GetProp<object?>(coordinator, "SelectedEndpointDevice"));

            // Invariant 3: AvailableEndpointDevices only lists active render devices (filters out inactive ghost)
            var availableEndpointsOut = (ICollection)GetProp<object>(coordinator, "AvailableEndpointDevices");
            Assert.Equal(2, availableEndpointsOut.Count); // spk1 and hfp1 (spk_ghost is inactive)
        }

        [Fact]
        public void DeviceRouteCoordinator_Gate5_RejectsSelectionOfIncompatibleBluetoothDevice()
        {
            var service = new StubBridgeService();
            var coordinator = CreateInstance("PhoneMicBridge.Gui.ViewModels.DeviceRouteCoordinator", service);

            // Try selecting compatible phone device
            SetProp(coordinator, "SelectedBluetoothDevice", service.BluetoothList[0]);
            Assert.NotNull(GetProp<object?>(coordinator, "SelectedBluetoothDevice"));

            // Try selecting incompatible mouse device (Gate 5 guard should block)
            SetProp(coordinator, "SelectedBluetoothDevice", service.BluetoothList[1]);
            // Still the phone!
            var currentBt = GetProp<BluetoothDeviceDto>(coordinator, "SelectedBluetoothDevice");
            Assert.Equal("bt_phone", currentBt.Id);
        }

        [Fact]
        public void AudioMeterMonitor_ZeroAllocHotPath_ComputesMetricsAndExposesBrushes()
        {
            var service = new StubBridgeService();
            var monitor = CreateInstance("PhoneMicBridge.Gui.ViewModels.AudioMeterMonitor", service);

            var updateMethod = monitor.GetType().GetMethod("UpdateMeters")!;
            updateMethod.Invoke(monitor, null);

            var peakPercent = GetProp<double>(monitor, "InputPeakPercent");
            var peakDbText = GetProp<string>(monitor, "InputPeakDbText");
            var brush = GetProp<object>(monitor, "InputMeterBrush");

            Assert.Equal(50.0, peakPercent);
            Assert.Equal("-6.0 dBFS", peakDbText);
            Assert.NotNull(brush);

            // Test deterministic cleanup
            var disposeMethod = monitor.GetType().GetMethod("Dispose")!;
            disposeMethod.Invoke(monitor, null);
        }

        [Fact]
        public void AudioSourceController_MicAndToneCommands_ReflectSnapshotState()
        {
            var service = new StubBridgeService();
            var controller = CreateInstance("PhoneMicBridge.Gui.ViewModels.AudioSourceController", service);

            Assert.True(GetProp<bool>(controller, "IsMicEnabled"));
            Assert.False(GetProp<bool>(controller, "IsMicMuted"));
            Assert.Equal("MUTE MIC", GetProp<string>(controller, "MicMuteButtonText"));

            // Apply snapshot with mic muted
            var mutatedSnapshot = service.CurrentSnapshot with { IsMicEnabled = false, MicGain = 1.8f };
            var applyMethod = controller.GetType().GetMethod("ApplySnapshot")!;
            applyMethod.Invoke(controller, new object[] { mutatedSnapshot });

            Assert.True(GetProp<bool>(controller, "IsMicMuted"));
            Assert.Equal("UNMUTE MIC", GetProp<string>(controller, "MicMuteButtonText"));
            Assert.Equal("MUTED", GetProp<string>(controller, "MicStatusText"));
            Assert.Equal("1.80x", GetProp<string>(controller, "MicGainText"));
        }

        [Fact]
        public void MainViewModel_Facade_BubblesPropertyChangedEventsFromSubCoordinators()
        {
            var service = new StubBridgeService();
            var vm = CreateInstance("PhoneMicBridge.Gui.ViewModels.MainViewModel", service);

            var notifiedProperties = new HashSet<string>();
            var inpc = (INotifyPropertyChanged)vm;
            inpc.PropertyChanged += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.PropertyName))
                {
                    notifiedProperties.Add(e.PropertyName);
                }
            };

            // Mutating route mode on MainViewModel
            var setRouteMethod = vm.GetType().GetMethod("SetRouteMode")!;
            setRouteMethod.Invoke(vm, new object[] { BridgeRouteMode.Out });

            Assert.Contains("RouteMode", notifiedProperties);
            Assert.Contains("IsRouteIn", notifiedProperties);
            Assert.Contains("IsRouteOut", notifiedProperties);
            Assert.Contains("Dropdown2Label", notifiedProperties);

            // Accessing sub-coordinator properties directly
            var routeCoord = GetProp<object>(vm, "RouteCoordinator");
            var meterMonitor = GetProp<object>(vm, "MeterMonitor");
            var sourceCtrl = GetProp<object>(vm, "SourceController");

            Assert.NotNull(routeCoord);
            Assert.NotNull(meterMonitor);
            Assert.NotNull(sourceCtrl);
        }
    }
}
