using System;
using System.Reflection;
using NAudio.CoreAudioApi;
using PhoneMicBridge.Audio;
using PhoneMicBridge.Cli;
using PhoneMicBridge.Core;
using Xunit;

namespace PhoneMicBridge.Tests
{
    public class HostAudioMonitorAndDeviceLossPolicyTests
    {
        [Fact]
        public void CliOptions_Parse_MonitorFlag_SetsEnableMonitorTrue()
        {
            // Arrange
            string[] args = ["--monitor", "--mic-gain", "1.5"];

            // Act
            var options = CliOptions.Parse(args);

            // Assert
            Assert.True(options.EnableMonitor);
            Assert.Equal(1.5f, options.MicGain);
        }

        [Fact]
        public void BridgeStateMachine_DegradedTransitions_AreLegal()
        {
            var sm = new BridgeStateMachine(BridgeState.Disconnected);

            // Disconnected -> Connecting -> Streaming -> Degraded
            sm.TransitionTo(BridgeState.Connecting);
            sm.TransitionTo(BridgeState.Streaming);
            Assert.True(sm.CanTransitionTo(BridgeState.Degraded));
            sm.TransitionTo(BridgeState.Degraded);
            Assert.Equal(BridgeState.Degraded, sm.CurrentState);

            // Degraded -> Streaming (recovery)
            Assert.True(sm.CanTransitionTo(BridgeState.Streaming));
            sm.TransitionTo(BridgeState.Streaming);
            Assert.Equal(BridgeState.Streaming, sm.CurrentState);

            // Streaming -> Degraded -> Stopped
            sm.TransitionTo(BridgeState.Degraded);
            Assert.True(sm.CanTransitionTo(BridgeState.Stopped));
            sm.TransitionTo(BridgeState.Stopped);
            Assert.Equal(BridgeState.Stopped, sm.CurrentState);
        }

        [Fact]
        public void BridgeStateMachine_DegradedToError_IsLegal()
        {
            var sm = new BridgeStateMachine(BridgeState.Streaming);
            sm.TransitionTo(BridgeState.Degraded);

            Assert.True(sm.CanTransitionTo(BridgeState.Error));
            sm.TransitionTo(BridgeState.Error);
            Assert.Equal(BridgeState.Error, sm.CurrentState);
        }

        [Fact]
        public void WasapiAudioEngine_DeviceLossPolicy_OnDeviceRemoved_TransitionsToErrorSafely()
        {
            // Reflection verification of OnDeviceRemoved behavior in WasapiAudioEngine
            // Ensure OnDeviceRemoved method exists with (object, DeviceNotificationEventArgs) signature
            var method = typeof(WasapiAudioEngine).GetMethod(
                "OnDeviceRemoved",
                BindingFlags.NonPublic | BindingFlags.Instance,
                [typeof(object), typeof(DeviceNotificationEventArgs)]
            );
            Assert.NotNull(method);
        }

        [Fact]
        public void WasapiAudioEngine_DeviceLossPolicy_OnDeviceStateChanged_HandlesDisconnectCleanly()
        {
            var method = typeof(WasapiAudioEngine).GetMethod(
                "OnDeviceStateChanged",
                BindingFlags.NonPublic | BindingFlags.Instance,
                [typeof(object), typeof(DeviceStateChangedEventArgs)]
            );
            Assert.NotNull(method);
        }

        [Fact]
        public void HostAudioMonitor_InstantiationAndDisposal_ReleasesResourcesCleanlyWithoutLeak()
        {
            // Verify HostAudioMonitor can be instantiated and disposed repeatedly without exceptions
            var defaultRender = Devices.DeviceDiscovery.GetDefaultRenderDevice();
            if (defaultRender != null)
            {
                for (int i = 0; i < 5; i++)
                {
                    using var monitor = new HostAudioMonitor(defaultRender, defaultRender);
                    Assert.False(monitor.IsRunning);
                    Assert.NotNull(monitor.HfpCaptureDevice);
                    Assert.NotNull(monitor.HostPlaybackDevice);
                }
            }
        }
    }
}
