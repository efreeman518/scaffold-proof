using System.Collections.Concurrent;
using TaskFlow.Application.Contracts.Locking;

namespace TaskFlow.Infrastructure.Caching.Locking;

/// <summary>
/// Per-key in-process lock, used when no Redis connection is configured (D-052).
/// <para>
/// Ceiling: this excludes threads inside one process, not replicas. That is exactly right on a single
/// replica - the local dev and test shape - and silently wrong on more than one, which is why the choice is
/// made once at registration from whether Redis is configured, not per call site.
/// </para>
/// </summary>
public sealed class InProcessDistributedLock : IDistributedLock
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    /// <inheritdoc />
    /// <remarks>
    /// The ttl is ignored, and there is nothing for it to do: a semaphore cannot outlive the process holding
    /// it, so the crashed-holder case a ttl exists to bound cannot happen here.
    /// </remarks>
    public async ValueTask<IAsyncDisposable?> TryAcquireAsync(
        string key, TimeSpan ttl, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ct.ThrowIfCancellationRequested();

        var gate = _gates.GetOrAdd(key, static _ => new SemaphoreSlim(1, 1));

        // Zero timeout: a try-acquire, never a wait. WaitAsync rather than the synchronous overload so
        // there is no path here that occupies a thread, even one that would return immediately.
        return await gate.WaitAsync(millisecondsTimeout: 0, ct).ConfigureAwait(false)
            ? new Handle(gate)
            : null;
    }

    private sealed class Handle(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync()
        {
            gate.Release();
            return ValueTask.CompletedTask;
        }
    }
}
