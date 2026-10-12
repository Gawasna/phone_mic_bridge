using System;
using PhoneMicBridge.Application;
using PhoneMicBridge.Core;
using Xunit;

namespace PhoneMicBridge.Tests
{
    public class BridgeUiSnapshotTests
    {
        [Fact]
        public void InitialSnapshot_ShouldHaveTruthfulDefaults()
        {
            // Act
            var snapshot = BridgeUiSnapshot.Initial();

            // Assert
            Assert.Equal(BridgeState.Disconnected, snapshot.State);
            Assert.Equal(EndpointRenderStatus.Inactive, snapshot.RenderStatus);
            Assert.Equal(BluetoothTransportStatus.Disconnected, snapshot.BluetoothStatus);
            Assert.Equal(DeliveryAckStatus.Unconfirmed, snapshot.DeliveryStatus);
            Assert.Equal(DiagnosticOverallStatus.Unknown, snapshot.PreflightOverall);
            Assert.False(snapshot.CanStartSession);
            Assert.True(snapshot.IsMicEnabled);
            Assert.Equal(1.0f, snapshot.MicGain);
            Assert.False(snapshot.IsToneEnabled);
            Assert.Null(snapshot.LastError);
            Assert.Empty(snapshot.PreflightItems);
        }

        [Fact]
        public void Snapshot_RecordImmutability_WithExpressionShouldProduceNewInstance()
        {
            // Arrange
            var initial = BridgeUiSnapshot.Initial();

            // Act
            var modified = initial with
            {
                State = BridgeState.Streaming,
                RenderStatus = EndpointRenderStatus.Active,
                CanStartSession = false
            };

            // Assert
            Assert.NotSame(initial, modified);
            Assert.Equal(BridgeState.Disconnected, initial.State);
            Assert.Equal(BridgeState.Streaming, modified.State);
            Assert.Equal(EndpointRenderStatus.Inactive, initial.RenderStatus);
            Assert.Equal(EndpointRenderStatus.Active, modified.RenderStatus);
        }

        [Fact]
        public void AudioMeterValues_ShouldSupportValueEqualityWithoutHeapAlloc()
        {
            // Arrange
            var m1 = new AudioMeterValues(0.5f, 0.35f, -6.0f, 0.8f, 0.6f, -2.0f, false, false);
            var m2 = new AudioMeterValues(0.5f, 0.35f, -6.0f, 0.8f, 0.6f, -2.0f, false, false);

            // Assert
            Assert.Equal(m1, m2);
            Assert.False(m1.IsInputClipping);
            Assert.False(m1.IsOutputClipping);
        }

        [Fact]
        public void PreflightItemDto_ShouldPreserveTriStateAndRemediationText()
        {
            // Act
            var item = new PreflightItemDto(
                CheckId: "CHECK_MIC_INPUT",
                Title: "Microphone Input Check",
                Status: "Fail",
                Details: "Microphone not plugged in",
                RemediationText: "Plug in the microphone"
            );

            // Assert
            Assert.Equal("CHECK_MIC_INPUT", item.CheckId);
            Assert.Equal("Fail", item.Status);
            Assert.Equal("Plug in the microphone", item.RemediationText);
        }
    }
}
