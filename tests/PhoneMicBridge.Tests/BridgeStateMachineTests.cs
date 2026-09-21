using System;
using PhoneMicBridge.Core;
using Xunit;

namespace PhoneMicBridge.Tests
{
    public class BridgeStateMachineTests
    {
        [Fact]
        public void InitialState_ShouldBeDisconnected()
        {
            var sm = new BridgeStateMachine();
            Assert.Equal(BridgeState.Disconnected, sm.CurrentState);
        }

        [Fact]
        public void LegalTransitions_ShouldSucceedAndFireEvent()
        {
            var sm = new BridgeStateMachine(BridgeState.Disconnected);
            BridgeState? capturedState = null;
            sm.StateChanged += (s, e) => capturedState = e;

            // Disconnected -> Connecting
            Assert.True(sm.CanTransitionTo(BridgeState.Connecting));
            sm.TransitionTo(BridgeState.Connecting);
            Assert.Equal(BridgeState.Connecting, sm.CurrentState);
            Assert.Equal(BridgeState.Connecting, capturedState);

            // Connecting -> Streaming
            Assert.True(sm.CanTransitionTo(BridgeState.Streaming));
            sm.TransitionTo(BridgeState.Streaming);
            Assert.Equal(BridgeState.Streaming, sm.CurrentState);

            // Streaming -> Paused
            sm.TransitionTo(BridgeState.Paused);
            Assert.Equal(BridgeState.Paused, sm.CurrentState);

            // Paused -> Stopped
            sm.TransitionTo(BridgeState.Stopped);
            Assert.Equal(BridgeState.Stopped, sm.CurrentState);
        }

        [Fact]
        public void IllegalTransition_ShouldThrowException()
        {
            var sm = new BridgeStateMachine(BridgeState.Disconnected);

            // Disconnected -> Streaming directly is illegal (must go through Connecting)
            Assert.False(sm.CanTransitionTo(BridgeState.Streaming));
            Assert.Throws<InvalidStateTransitionException>(() => sm.TransitionTo(BridgeState.Streaming));
            Assert.Equal(BridgeState.Disconnected, sm.CurrentState);
        }

        [Fact]
        public void TryTransitionTo_WithIllegalState_ShouldReturnFalseWithoutThrowing()
        {
            var sm = new BridgeStateMachine(BridgeState.Disconnected);

            bool result = sm.TryTransitionTo(BridgeState.Streaming);
            Assert.False(result);
            Assert.Equal(BridgeState.Disconnected, sm.CurrentState);
        }
    }
}
