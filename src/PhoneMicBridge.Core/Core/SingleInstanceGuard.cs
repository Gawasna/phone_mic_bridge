using System;
using System.Threading;

namespace PhoneMicBridge.Core
{
    /// <summary>
    /// Contract for enforcing single-instance mutual exclusion at process boundary.
    /// </summary>
    public interface ISingleInstanceGuard : IDisposable
    {
        /// <summary>
        /// Gets whether this instance currently holds exclusive ownership of the synchronization primitive.
        /// </summary>
        bool IsOwned { get; }

        /// <summary>
        /// Attempts to acquire exclusive ownership within the specified timeout.
        /// </summary>
        /// <param name="timeout">Duration to wait for acquisition. Default is zero (instant non-blocking probe).</param>
        /// <returns>True if exclusive ownership is acquired; false if another instance holds it.</returns>
        bool TryAcquire(TimeSpan timeout = default);

        /// <summary>
        /// Explicitly releases the acquired ownership without disposing the underlying primitive.
        /// </summary>
        void Release();
    }

    /// <summary>
    /// OS-level single instance guardian using Windows Mutex primitive.
    /// Scoped to the local user logon session (Local\) to adhere to minimum authority invariants.
    /// Handles AbandonedMutexException gracefully when previous process crashed unexpectedly.
    /// </summary>
    public sealed class SingleInstanceGuard : ISingleInstanceGuard
    {
        public const string DefaultIdentifier = @"Local\PhoneMicBridge_SingleInstance_Mutex_Gawasna";

        private readonly string _mutexName;
        private Mutex? _mutex;
        private bool _isOwned;
        private bool _disposed;

        public bool IsOwned => _isOwned;

        public SingleInstanceGuard(string? identifier = null)
        {
            if (string.IsNullOrWhiteSpace(identifier))
            {
                _mutexName = DefaultIdentifier;
            }
            else
            {
                // Ensure proper session scoping if not explicitly prefixed
                _mutexName = identifier.StartsWith(@"Local\", StringComparison.OrdinalIgnoreCase) ||
                             identifier.StartsWith(@"Global\", StringComparison.OrdinalIgnoreCase)
                    ? identifier
                    : $@"Local\{identifier}";
            }
        }

        public bool TryAcquire(TimeSpan timeout = default)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_isOwned)
            {
                return true;
            }

            try
            {
                _mutex = new Mutex(true, _mutexName, out bool createdNew);
                if (createdNew)
                {
                    _isOwned = true;
                    return true;
                }

                if (timeout > TimeSpan.Zero)
                {
                    try
                    {
                        _isOwned = _mutex.WaitOne(timeout, false);
                    }
                    catch (AbandonedMutexException)
                    {
                        _isOwned = true;
                    }
                }
                else
                {
                    _isOwned = false;
                }

                return _isOwned;
            }
            catch (AbandonedMutexException)
            {
                _isOwned = true;
                return true;
            }
        }

        public void Release()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_isOwned && _mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                finally
                {
                    _isOwned = false;
                }
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (_isOwned && _mutex != null)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Thread does not own the mutex or it has already been released
                }
                _isOwned = false;
            }

            _mutex?.Dispose();
            _mutex = null;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}
