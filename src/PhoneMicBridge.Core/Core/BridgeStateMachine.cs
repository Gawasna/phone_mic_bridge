using System;

namespace PhoneMicBridge.Core
{
    public class InvalidStateTransitionException : InvalidOperationException
    {
        public BridgeState From { get; }
        public BridgeState To { get; }

        public InvalidStateTransitionException(BridgeState from, BridgeState to)
            : base($"Illegal state transition from '{from}' to '{to}'. Invariant violated.")
        {
            From = from;
            To = to;
        }
    }

    /// <summary>
    /// Enforces explicit authority over BridgeState transitions in accordance with OS-Thinking discipline.
    /// Eliminates arbitrary state mutations and validates legal transition edges.
    /// </summary>
    public class BridgeStateMachine
    {
        private readonly object _lock = new();
        private BridgeState _currentState;

        public BridgeState CurrentState
        {
            get
            {
                lock (_lock) return _currentState;
            }
        }

        public event EventHandler<BridgeState>? StateChanged;

        public BridgeStateMachine(BridgeState initialState = BridgeState.Disconnected)
        {
            _currentState = initialState;
        }

        public bool CanTransitionTo(BridgeState nextState)
        {
            lock (_lock)
            {
                return IsLegalTransition(_currentState, nextState);
            }
        }

        public void TransitionTo(BridgeState nextState)
        {
            BridgeState oldState;
            lock (_lock)
            {
                if (_currentState == nextState) return;

                if (!IsLegalTransition(_currentState, nextState))
                {
                    throw new InvalidStateTransitionException(_currentState, nextState);
                }

                oldState = _currentState;
                _currentState = nextState;
            }

            StateChanged?.Invoke(this, nextState);
        }

        public bool TryTransitionTo(BridgeState nextState)
        {
            BridgeState oldState;
            lock (_lock)
            {
                if (_currentState == nextState) return true;
                if (!IsLegalTransition(_currentState, nextState)) return false;

                oldState = _currentState;
                _currentState = nextState;
            }

            StateChanged?.Invoke(this, nextState);
            return true;
        }

        private static bool IsLegalTransition(BridgeState from, BridgeState to)
        {
            return (from, to) switch
            {
                (BridgeState.Disconnected, BridgeState.Connecting) => true,
                (BridgeState.Connecting, BridgeState.Streaming) => true,
                (BridgeState.Connecting, BridgeState.Stopped) => true,
                (BridgeState.Connecting, BridgeState.Degraded) => true,
                (BridgeState.Connecting, BridgeState.Error) => true,

                (BridgeState.Streaming, BridgeState.Paused) => true,
                (BridgeState.Streaming, BridgeState.Stopped) => true,
                (BridgeState.Streaming, BridgeState.Degraded) => true,
                (BridgeState.Streaming, BridgeState.Error) => true,

                (BridgeState.Paused, BridgeState.Streaming) => true,
                (BridgeState.Paused, BridgeState.Stopped) => true,
                (BridgeState.Paused, BridgeState.Degraded) => true,
                (BridgeState.Paused, BridgeState.Error) => true,

                (BridgeState.Stopped, BridgeState.Connecting) => true,
                (BridgeState.Stopped, BridgeState.Disconnected) => true,

                (BridgeState.Degraded, BridgeState.Connecting) => true,
                (BridgeState.Degraded, BridgeState.Streaming) => true,
                (BridgeState.Degraded, BridgeState.Stopped) => true,
                (BridgeState.Degraded, BridgeState.Error) => true,
                (BridgeState.Degraded, BridgeState.Disconnected) => true,

                (BridgeState.Error, BridgeState.Connecting) => true,
                (BridgeState.Error, BridgeState.Stopped) => true,
                (BridgeState.Error, BridgeState.Disconnected) => true,

                _ => false
            };
        }
    }
}
