namespace TaskFlow.Application.Contracts.Locking;

/// <summary>
/// Mutual exclusion across replicas for work that must not run twice concurrently (D-052).
/// <para>
/// Scoped to one-time startup tasks - external resource provisioning, broker topology declaration. Work
/// tables are not in scope: they already coordinate through leases and conditional updates (D-026), which
/// survive a crash mid-work in a way a lock with a ttl does not.
/// </para>
/// </summary>
public interface IDistributedLock
{
    /// <summary>
    /// Takes the lock if it is free. Non-blocking by design: every caller here has something better to do
    /// than queue, and a blocking acquire hides how long startup actually waited.
    /// </summary>
    /// <param name="key">Lock identity, shared by every replica competing for the same work.</param>
    /// <param name="ttl">
    /// How long the lock survives without being released. It must outlive the work: a holder that overruns
    /// loses the lock while still working. It also bounds how long a crashed holder blocks everyone else.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A handle whose disposal releases the lock, or null when another holder has it.</returns>
    ValueTask<IAsyncDisposable?> TryAcquireAsync(string key, TimeSpan ttl, CancellationToken ct = default);
}
