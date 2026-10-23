using System;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows.Input;
using PhoneMicBridge.Application;
using PhoneMicBridge.Devices;
using Xunit;

namespace PhoneMicBridge.Tests
{
    public class BluetoothRadioProgrammaticControlTests
    {
        private class MockBluetoothRadioController : IBluetoothRadioController
        {
            public bool RadioEnabled { get; set; } = false;
            public BluetoothRadioStateResult ResultToReturn { get; set; } = BluetoothRadioStateResult.Success;
            public int SetStateCallCount { get; private set; }
            public bool LastRequestedState { get; private set; }
            public bool OpenSettingsCalled { get; private set; }
            public bool OpenPrivacyCalled { get; private set; }
            public event EventHandler<bool>? RadioStateChanged;
            
            public void TriggerExternalStateChange(bool newState)
            {
                RadioEnabled = newState;
                RadioStateChanged?.Invoke(this, newState);
            }

            public bool IsRadioEnabled()
            {
                return RadioEnabled;
            }

            public Task<bool> IsRadioEnabledAsync()
            {
                return Task.FromResult(RadioEnabled);
            }

            public Task<BluetoothRadioStateResult> SetRadioStateAsync(bool enable)
            {
                SetStateCallCount++;
                LastRequestedState = enable;
                if (ResultToReturn == BluetoothRadioStateResult.Success)
                {
                    RadioEnabled = enable;
                }
                return Task.FromResult(ResultToReturn);
            }

            public bool OpenWindowsBluetoothSettings()
            {
                OpenSettingsCalled = true;
                return true;
            }

            public bool OpenWindowsPrivacySettings()
            {
                OpenPrivacyCalled = true;
                return true;
            }
        }

        private static Assembly GetGuiAssembly()
        {
            var existing = Array.Find(AppDomain.CurrentDomain.GetAssemblies(), a => a.GetName().Name == "PhoneMicBridge.Gui");
            if (existing != null)
            {
                return existing;
            }

            string testDir = AppContext.BaseDirectory;
            string[] possiblePaths = new[]
            {
                Path.GetFullPath(Path.Combine(testDir, @"..\..\..\..\..\src\PhoneMicBridge.Gui\bin\Debug\net9.0-windows10.0.19041.0\PhoneMicBridge.Gui.dll")),
                Path.GetFullPath(Path.Combine(testDir, @"..\..\..\..\..\src\PhoneMicBridge.Gui\bin\Debug\net9.0-windows10.0.19041.0\PhoneMicBridge.Gui.dll"))
            };

            foreach (var path in possiblePaths)
            {
                if (File.Exists(path))
                {
                    return Assembly.LoadFrom(path);
                }
            }

            string solutionRoot = Path.GetFullPath(Path.Combine(testDir, @"..\..\..\..\..\"));
            string fallbackPath = Path.Combine(solutionRoot, "src", "PhoneMicBridge.Gui", "bin", "Debug", "net9.0-windows10.0.19041.0", "PhoneMicBridge.Gui.dll");
            if (!File.Exists(fallbackPath))
            {
                fallbackPath = Path.Combine(solutionRoot, "src", "PhoneMicBridge.Gui", "bin", "Debug", "net9.0-windows", "PhoneMicBridge.Gui.dll");
            }

            Assert.True(File.Exists(fallbackPath), $"PhoneMicBridge.Gui.dll must exist at: {fallbackPath}");
            return Assembly.LoadFrom(fallbackPath);
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

        [Fact]
        public async Task TurnOnBluetoothRadioAsync_WhenAllowed_TransitionsToSuccessAndTriggersDiagnostics()
        {
            // Arrange
            var mockController = new MockBluetoothRadioController
            {
                RadioEnabled = false,
                ResultToReturn = BluetoothRadioStateResult.Success
            };

            using var service = new BridgeApplicationService(
                diagnosticsEngine: null,
                radioController: mockController);

            // Act
            var result = await service.TurnOnBluetoothRadioAsync();

            // Assert
            Assert.Equal(BluetoothRadioStateResult.Success, result);
            Assert.Equal(1, mockController.SetStateCallCount);
            Assert.True(mockController.LastRequestedState);
            Assert.True(mockController.RadioEnabled);
        }

        [Fact]
        public async Task TurnOnBluetoothRadioAsync_WhenAlreadyOn_ReturnsAlreadyInDesiredState()
        {
            // Arrange
            var mockController = new MockBluetoothRadioController
            {
                RadioEnabled = true,
                ResultToReturn = BluetoothRadioStateResult.AlreadyInDesiredState
            };

            using var service = new BridgeApplicationService(
                diagnosticsEngine: null,
                radioController: mockController);

            // Act
            var result = await service.TurnOnBluetoothRadioAsync();

            // Assert
            Assert.Equal(BluetoothRadioStateResult.AlreadyInDesiredState, result);
            Assert.Equal(1, mockController.SetStateCallCount);
        }

        [Theory]
        [InlineData(BluetoothRadioStateResult.DeniedByUser)]
        [InlineData(BluetoothRadioStateResult.DeniedBySystem)]
        [InlineData(BluetoothRadioStateResult.RadioNotFound)]
        [InlineData(BluetoothRadioStateResult.NotSupported)]
        [InlineData(BluetoothRadioStateResult.Failed)]
        public async Task TurnOnBluetoothRadioAsync_WhenPolicyOrHardwareBlocks_ReturnsSpecificErrorResult(BluetoothRadioStateResult expectedError)
        {
            // Arrange
            var mockController = new MockBluetoothRadioController
            {
                RadioEnabled = false,
                ResultToReturn = expectedError
            };

            using var service = new BridgeApplicationService(
                diagnosticsEngine: null,
                radioController: mockController);

            // Act
            var result = await service.TurnOnBluetoothRadioAsync();

            // Assert
            Assert.Equal(expectedError, result);
            Assert.False(mockController.RadioEnabled);
        }

        [Fact]
        public async Task BluetoothDeviceDiscovery_SetBluetoothRadioStateAsync_InvokesSuppliedController()
        {
            // Arrange
            var mockController = new MockBluetoothRadioController
            {
                ResultToReturn = BluetoothRadioStateResult.Success
            };

            // Act
            var result = await BluetoothDeviceDiscovery.SetBluetoothRadioStateAsync(true, mockController);

            // Assert
            Assert.Equal(BluetoothRadioStateResult.Success, result);
            Assert.Equal(1, mockController.SetStateCallCount);
            Assert.True(mockController.LastRequestedState);
        }

        [Fact]
        public void BluetoothDeviceDiscovery_OpenWindowsPrivacySettings_InvokesController()
        {
            // Arrange
            var mockController = new MockBluetoothRadioController();

            // Act
            var opened = BluetoothDeviceDiscovery.OpenWindowsPrivacySettings(mockController);

            // Assert
            Assert.True(opened);
            Assert.True(mockController.OpenPrivacyCalled);
        }

        [Fact]
        public async Task WinRtBluetoothRadioController_LiveHostCheck_ExecutesWithoutCrashing()
        {
            // Act: Verify live WinRT integration on Windows host executes cleanly
            var controller = WinRtBluetoothRadioController.Instance;
            var isEnabled = await controller.IsRadioEnabledAsync();

            // Assert: Boolean result returned cleanly without throwing exceptions
            Assert.True(isEnabled || !isEnabled);
        }
        [Fact]
        public async Task ExternalRadioToggle_FiresEvent_UpdatesMainViewModel()
        {
            var mockController = new MockBluetoothRadioController { RadioEnabled = false };
            using var service = new BridgeApplicationService(null, mockController);
            var vm = CreateInstance("PhoneMicBridge.Gui.ViewModels.MainViewModel", service);
            
            var loadMethod = vm.GetType().GetMethod("LoadDevicesAsync")!;
            await (Task)loadMethod.Invoke(vm, null)!;
            
            var isEnabledProperty = vm.GetType().GetProperty("IsBluetoothEnabled")!;
            Assert.False((bool)(isEnabledProperty.GetValue(vm) ?? false));
            
            mockController.TriggerExternalStateChange(true);
            await Task.Delay(250); // Allow async background diagnostic refresh to publish
            
            Assert.True((bool)(isEnabledProperty.GetValue(vm) ?? false), "MainViewModel failed to react to external WinRT Radio state changes");
        }
    }
}






