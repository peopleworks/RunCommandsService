using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace RunCommandsService
{
    /// <summary>
    /// Simple keyed concurrency guard. When AllowParallelRuns=false, only one job with the same key may run at a time.
    /// Returns an IDisposable token when the slot is acquired; returns null if the slot is busy.
    /// </summary>
    public class ConcurrencyManager
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _locks = new();

        public Task<IDisposable> TryAcquireAsync(string key, bool allowParallelRuns, CancellationToken ct)
        {
            if (allowParallelRuns)
            {
                // No lock needed, but return a non-null token so caller can `using` it uniformly.
                return Task.FromResult<IDisposable>(Noop.Instance);
            }

            var k = key ?? string.Empty;
            var sem = _locks.GetOrAdd(k, _ => new SemaphoreSlim(1, 1));

            // Non-blocking attempt; if busy, return null to signal "Skipped (lock)".
            if (!sem.Wait(0, ct))
                return Task.FromResult<IDisposable>(null);

            return Task.FromResult<IDisposable>(new Releaser(sem));
        }

        private sealed class Releaser : IDisposable
        {
            private readonly SemaphoreSlim _sem;
            private bool _disposed;
            public Releaser(SemaphoreSlim sem) => _sem = sem;
            public void Dispose()
            {
                if (_disposed) return;
                _disposed = true;
                try { _sem?.Release(); } catch { /* ignore */ }
            }
        }

        private sealed class Noop : IDisposable
        {
            public static readonly Noop Instance = new Noop();
            private Noop() { }
            public void Dispose() { /* nothing */ }
        }
    }
}
