using System;
using System.Threading;
using PhoneMicBridge.Core;
using Xunit;

namespace PhoneMicBridge.Tests
{
    public class SingleInstanceGuardTests
    {
        private static T RunOnDedicatedThread<T>(Func<T> action)
        {
            T result = default!;
            Exception? caught = null;
            var thread = new Thread(() =>
            {
                try
                {
                    result = action();
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
            });
            thread.Start();
            thread.Join();
            if (caught != null) throw new InvalidOperationException("Exception on dedicated thread", caught);
            return result;
        }

        [Fact]
        public void TryAcquire_FirstInstance_AcquiresExclusiveOwnership()
        {
            var id = $"Test_SingleInstance_{Guid.NewGuid():N}";
            using var guard = new SingleInstanceGuard(id);

            var acquired = guard.TryAcquire();

            Assert.True(acquired);
            Assert.True(guard.IsOwned);
        }

        [Fact]
        public void TryAcquire_SecondInstanceWithSameIdentifier_FailsToAcquire()
        {
            var id = $"Test_SingleInstance_{Guid.NewGuid():N}";
            using var firstGuard = new SingleInstanceGuard(id);
            using var secondGuard = new SingleInstanceGuard(id);

            var firstAcquired = firstGuard.TryAcquire();
            // Mutex is re-entrant per OS thread; test cross-thread/cross-process isolation via separate thread
            var secondAcquired = RunOnDedicatedThread(() => secondGuard.TryAcquire(TimeSpan.FromMilliseconds(50)));

            Assert.True(firstAcquired);
            Assert.False(secondAcquired);
            Assert.True(firstGuard.IsOwned);
            Assert.False(secondGuard.IsOwned);
        }

        [Fact]
        public void Release_AllowsSubsequentInstanceToAcquire()
        {
            var id = $"Test_SingleInstance_{Guid.NewGuid():N}";
            using var firstGuard = new SingleInstanceGuard(id);
            using var secondGuard = new SingleInstanceGuard(id);

            Assert.True(firstGuard.TryAcquire());
            Assert.False(RunOnDedicatedThread(() => secondGuard.TryAcquire(TimeSpan.FromMilliseconds(20))));

            firstGuard.Release();
            Assert.False(firstGuard.IsOwned);

            var secondAcquired = RunOnDedicatedThread(() => secondGuard.TryAcquire(TimeSpan.FromMilliseconds(100)));
            Assert.True(secondAcquired);
            Assert.True(secondGuard.IsOwned);
        }

        [Fact]
        public void Dispose_ReleasesOwnershipAutomatically()
        {
            var id = $"Test_SingleInstance_{Guid.NewGuid():N}";
            var firstGuard = new SingleInstanceGuard(id);
            using var secondGuard = new SingleInstanceGuard(id);

            Assert.True(firstGuard.TryAcquire());
            Assert.False(RunOnDedicatedThread(() => secondGuard.TryAcquire(TimeSpan.FromMilliseconds(20))));

            // Deterministic cleanup via Dispose
            firstGuard.Dispose();

            var secondAcquired = RunOnDedicatedThread(() => secondGuard.TryAcquire(TimeSpan.FromMilliseconds(100)));
            Assert.True(secondAcquired);
            Assert.True(secondGuard.IsOwned);
        }

        [Fact]
        public void DistinctIdentifiers_DoNotCollide()
        {
            var id1 = $"Test_SingleInstance_{Guid.NewGuid():N}";
            var id2 = $"Test_SingleInstance_{Guid.NewGuid():N}";

            using var guard1 = new SingleInstanceGuard(id1);
            using var guard2 = new SingleInstanceGuard(id2);

            Assert.True(guard1.TryAcquire());
            Assert.True(guard2.TryAcquire());
            Assert.True(guard1.IsOwned);
            Assert.True(guard2.IsOwned);
        }

        [Fact]
        public void TryAcquire_WhenAlreadyOwned_ReturnsTrueImmediately()
        {
            var id = $"Test_SingleInstance_{Guid.NewGuid():N}";
            using var guard = new SingleInstanceGuard(id);

            Assert.True(guard.TryAcquire());
            Assert.True(guard.TryAcquire());
            Assert.True(guard.IsOwned);
        }

        [Fact]
        public void Dispose_IsIdempotent()
        {
            var id = $"Test_SingleInstance_{Guid.NewGuid():N}";
            var guard = new SingleInstanceGuard(id);
            guard.TryAcquire();

            // Calling Dispose multiple times must not throw
            guard.Dispose();
            guard.Dispose();
        }

        [Fact]
        public void OperationsAfterDispose_ThrowObjectDisposedException()
        {
            var id = $"Test_SingleInstance_{Guid.NewGuid():N}";
            var guard = new SingleInstanceGuard(id);
            guard.Dispose();

            Assert.Throws<ObjectDisposedException>(() => guard.TryAcquire());
            Assert.Throws<ObjectDisposedException>(() => guard.Release());
        }
    }
}
