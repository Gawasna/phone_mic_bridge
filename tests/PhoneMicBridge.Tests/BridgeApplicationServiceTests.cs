using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using PhoneMicBridge.Application;
using PhoneMicBridge.Core;
using Xunit;

namespace PhoneMicBridge.Tests
{
    public class BridgeApplicationServiceTests
    {
        private class StubDiagnosticsEngine : IPreflightDiagnosticsEngine
        {
            public IReadOnlyList<PreflightItemDto> ItemsToReturn { get; set; } = Array.Empty<PreflightItemDto>();

            public Task<IReadOnlyList<PreflightItemDto>> ExecuteChecksAsync(
                string? captureDeviceId,
                string? renderDeviceId,
                CancellationToken cancellationToken = default)
            {
                return Task.FromResult(ItemsToReturn);
            }
        }

        [Fact]
        public async Task RunPreflightDiagnosticsAsync_WhenAllChecksPass_ShouldSetOverallToPassAndAllowStart()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine
            {
                ItemsToReturn = new List<PreflightItemDto>
                {
                    new("CHECK_MIC_INPUT", "Mic Check", "Pass", "Ready", "None"),
                    new("CHECK_RENDER_DEVICE", "Render Check", "Pass", "Ready", "None"),
                    new("CHECK_BLUETOOTH_TRANSPORT", "BT Check", "Pass", "Ready", "None")
                }
            };

            using var service = new BridgeApplicationService(stub);

            // Act
            var snapshot = await service.RunPreflightDiagnosticsAsync();

            // Assert
            Assert.Equal(DiagnosticOverallStatus.Pass, snapshot.PreflightOverall);
            Assert.True(snapshot.CanStartSession);
            Assert.Equal(3, snapshot.PreflightItems.Count);
        }

        [Fact]
        public async Task RunPreflightDiagnosticsAsync_WhenAnyCheckFails_ShouldSetOverallToFailAndBlockStart()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine
            {
                ItemsToReturn = new List<PreflightItemDto>
                {
                    new("CHECK_MIC_INPUT", "Mic Check", "Pass", "Ready", "None"),
                    new("CHECK_RENDER_DEVICE", "Render Check", "Fail", "Missing", "Pair phone"),
                    new("CHECK_BLUETOOTH_TRANSPORT", "BT Check", "Fail", "Transport down", "Turn on BT")
                }
            };

            using var service = new BridgeApplicationService(stub);

            // Act
            var snapshot = await service.RunPreflightDiagnosticsAsync();

            // Assert
            Assert.Equal(DiagnosticOverallStatus.Fail, snapshot.PreflightOverall);
            Assert.False(snapshot.CanStartSession);
        }

        [Fact]
        public async Task StartBridgeAsync_WhenDiagnosticsFailed_ShouldThrowSafetyException()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine
            {
                ItemsToReturn = new List<PreflightItemDto>
                {
                    new("CHECK_RENDER_DEVICE", "Render Check", "Fail", "Missing", "Pair phone")
                }
            };

            using var service = new BridgeApplicationService(stub);
            await service.RunPreflightDiagnosticsAsync();

            // Act & Assert
            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await service.StartBridgeAsync();
            });
        }

        [Fact]
        public void SetMicControls_ShouldUpdateSnapshotValues()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            // Act
            service.SetMicEnabled(false);
            service.SetMicGain(1.75f);

            // Assert
            Assert.False(service.CurrentSnapshot.IsMicEnabled);
            Assert.Equal(1.75f, service.CurrentSnapshot.MicGain);
        }

        [Fact]
        public void SetToneControls_ShouldUpdateSnapshotValues()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            // Act
            service.SetToneEnabled(true, frequency: 440.0, initialGain: 0.5f);

            // Assert
            Assert.True(service.CurrentSnapshot.IsToneEnabled);
            Assert.Equal(440.0, service.CurrentSnapshot.ToneFrequency);
            Assert.Equal(0.5f, service.CurrentSnapshot.ToneGain);
        }

        [Fact]
        public void GetCurrentMeterValues_WhenDisconnected_ShouldReturnSilentMeters()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            // Act
            var meters = service.GetCurrentMeterValues();

            // Assert
            Assert.Equal(0.0f, meters.InputPeak);
            Assert.Equal(0.0f, meters.OutputPeak);
            Assert.Equal(-60.0f, meters.InputPeakDb);
            Assert.Equal(-60.0f, meters.OutputPeakDb);
            Assert.False(meters.IsInputClipping);
            Assert.False(meters.IsOutputClipping);
        }

        [Fact]
        public async Task StopBridgeAsync_WhenAlreadyDisconnected_ShouldBeIdempotentNoOp()
        {
            // Arrange
            var stub = new StubDiagnosticsEngine();
            using var service = new BridgeApplicationService(stub);

            // Act
            await service.StopBridgeAsync();
            await service.StopBridgeAsync();

            // Assert
            Assert.Equal(BridgeState.Disconnected, service.CurrentSnapshot.State);
        }
    }
}
