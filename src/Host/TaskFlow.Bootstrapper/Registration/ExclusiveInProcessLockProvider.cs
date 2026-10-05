using System.Collections.Concurrent;
using EF.FlowEngine.Abstractions;

namespace TaskFlow.Bootstrapper;

/// <summary>
/// Makes the FlowEngine instance lease exclusive inside this process as well as across processes.
/// <para>
/// Third-party mitigation, EF.FlowEngine 1.0.202. The engine claims every execution with one per-process claimant id,
/// and the SQL lock provider treats a claim by the same claimant as already held, so it grants it again.
/// <c>StartAsync</c> saves a new instance as Running with no claim before it claims it, and the sweep resumes any
/// Running instance with no claim as crashed. When the sweep runs in that window, the same process executes the
/// instance twice at once: under CPU saturation a compliance-check-item child read its evidence and called the agent
/// twice, and a compliance-check parent started every child twice.
/// </para>
/// <para>
/// A claim by the process claimant is therefore refused while this process already holds it for that instance and
/// the lease has not expired, as a claim by another process would be. Every other claimant (the engine's per-child
/// signal claimants) goes straight to the inner provider. Remove this decorator when EF.FlowEngine claims a new
/// instance atomically with its first save, or stops granting a held lease to the same claimant again.
/// </para>
/// </summary>
internal sealed class ExclusiveInProcessLockProvider(
    IDistributedLockProvider inner, string processClaimantId, TimeProvider clock) : IDistributedLockProvider
{
    // Instance id -> expiry of the lease this process holds for it under the process claimant.
    private readonly ConcurrentDictionary<string, DateTimeOffset> _held = new(StringComparer.Ordinal);

    /// <inheritdoc />
    public async Task<bool> TryAcquireAsync(string instanceId, string claimantId, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        if (!IsProcessClaimant(claimantId))
            return await inner.TryAcquireAsync(instanceId, claimantId, leaseDuration, ct).ConfigureAwait(false);

        var now = clock.GetUtcNow();
        var expires = now + leaseDuration;
        // Reserve the instance in this process first, atomically; an expired reservation is taken over.
        while (!_held.TryAdd(instanceId, expires))
        {
            if (!_held.TryGetValue(instanceId, out var current)) continue;
            if (current > now) return false;
            if (_held.TryUpdate(instanceId, expires, current)) break;
        }

        var acquired = false;
        try
        {
            acquired = await inner.TryAcquireAsync(instanceId, claimantId, leaseDuration, ct).ConfigureAwait(false);
            return acquired;
        }
        finally
        {
            if (!acquired) _held.TryRemove(new KeyValuePair<string, DateTimeOffset>(instanceId, expires));
        }
    }

    /// <inheritdoc />
    public Task<bool> TryAcquireNewAsync(string instanceId, string claimantId, TimeSpan leaseDuration, CancellationToken ct = default) =>
        inner.TryAcquireNewAsync(instanceId, claimantId, leaseDuration, ct);

    /// <inheritdoc />
    public async Task<bool> RenewAsync(string instanceId, string claimantId, TimeSpan leaseDuration, CancellationToken ct = default)
    {
        var renewed = await inner.RenewAsync(instanceId, claimantId, leaseDuration, ct).ConfigureAwait(false);
        if (IsProcessClaimant(claimantId))
        {
            // A lost lease is free for the next claim, as it is for another process.
            if (renewed) _held[instanceId] = clock.GetUtcNow() + leaseDuration;
            else _held.TryRemove(instanceId, out _);
        }

        return renewed;
    }

    /// <inheritdoc />
    public async Task ReleaseAsync(string instanceId, string claimantId, CancellationToken ct = default)
    {
        await inner.ReleaseAsync(instanceId, claimantId, ct).ConfigureAwait(false);
        if (IsProcessClaimant(claimantId)) _held.TryRemove(instanceId, out _);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<string>> FindResumableInstanceIdsAsync(
        DateTimeOffset now, int batchSize, int partitionCount = 1, int partitionIndex = 0, CancellationToken ct = default) =>
        inner.FindResumableInstanceIdsAsync(now, batchSize, partitionCount, partitionIndex, ct);

    private bool IsProcessClaimant(string claimantId) => string.Equals(claimantId, processClaimantId, StringComparison.Ordinal);
}
