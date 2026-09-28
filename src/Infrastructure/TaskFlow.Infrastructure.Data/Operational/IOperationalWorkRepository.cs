namespace TaskFlow.Infrastructure.Data.Operational;

/// <summary>One claimed batch and the token that owns it.</summary>
/// <typeparam name="TWork">Work row type.</typeparam>
/// <param name="LeaseToken">Token every claimed row now carries; the only safe key for completion and release.</param>
/// <param name="Items">Rows this caller owns until the lease expires.</param>
public sealed record LeasedBatch<TWork>(Guid LeaseToken, IReadOnlyList<TWork> Items)
    where TWork : OperationalWorkBase
{
    /// <summary>An empty claim.</summary>
    public static LeasedBatch<TWork> Empty { get; } = new(Guid.Empty, []);
}

/// <summary>Pending and lag figures for the outbox health check.</summary>
/// <param name="Pending">Live rows not yet dispatched.</param>
/// <param name="DeadLettered">Rows parked after the attempt ceiling.</param>
/// <param name="Lag">Age of the oldest due row, or zero when nothing is due.</param>
/// <param name="BlobDeletePending">Deferred blob deletions still waiting for a worker.</param>
public sealed record OutboxBacklog(int Pending, int DeadLettered, TimeSpan Lag, int BlobDeletePending);

/// <summary>
/// Provider-neutral lease claim over the operational work tables (D-026). No provider branch: the single-statement
/// upgrades are documented in the implementation as comments.
/// </summary>
public interface IOperationalWorkRepository
{
    /// <summary>
    /// Claims up to <paramref name="batchSize"/> due rows whose lease is free or expired and that have attempts
    /// left. Before claiming, rows that used their last attempt and whose lease then expired (the worker crashed
    /// or hung on them) are dead-lettered, so a poison row cannot be re-leased forever.
    /// </summary>
    /// <param name="batchSize">Most rows to claim.</param>
    /// <param name="leaseDuration">How long the claim is owned.</param>
    /// <param name="maxAttempts">Attempt ceiling (<c>LeasedWorkerOptions.MaxAttempts</c>); each claim is one attempt.</param>
    /// <param name="owner">Replica identity written to the owner column.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<LeasedBatch<TWork>> ClaimAsync<TWork>(
        int batchSize, TimeSpan leaseDuration, int maxAttempts, string owner, CancellationToken ct)
        where TWork : OperationalWorkBase;

    /// <summary>
    /// Hard-deletes the rows that were handled successfully; the lease token guards against a stolen lease.
    /// Returns the rows deleted: fewer than <paramref name="ids"/> means the lease on the rest was lost.
    /// </summary>
    Task<int> CompleteAsync<TWork>(Guid leaseToken, IReadOnlyCollection<Guid> ids, CancellationToken ct)
        where TWork : OperationalWorkBase;

    /// <summary>
    /// Releases one row after a transient failure with an exponential backoff on <paramref name="attemptCount"/>.
    /// The attempt is not given back: the claim already counted it. False when the lease was lost.
    /// Whether to release or dead-letter is the caller's policy.
    /// </summary>
    Task<bool> ReleaseAsync<TWork>(Guid leaseToken, Guid id, int attemptCount, string error, CancellationToken ct)
        where TWork : OperationalWorkBase;

    /// <summary>
    /// Parks one row after a permanent failure or its last attempt. A dead-lettered row is kept: it is the only
    /// surviving copy of the event. False when the lease was lost.
    /// </summary>
    Task<bool> DeadLetterAsync<TWork>(Guid leaseToken, Guid id, string error, CancellationToken ct)
        where TWork : OperationalWorkBase;

    /// <summary>
    /// Returns rows the worker never got to (shutdown mid-batch) without waiting out the lease and without
    /// consuming the attempt the claim counted. Returns the rows abandoned.
    /// </summary>
    Task<int> AbandonAsync<TWork>(Guid leaseToken, IReadOnlyCollection<Guid> ids, CancellationToken ct)
        where TWork : OperationalWorkBase;

    /// <summary>Resets one dead-lettered row so the dispatcher picks it up again. False when it was not dead-lettered.</summary>
    Task<bool> RetryDeadLetteredAsync<TWork>(Guid id, CancellationToken ct)
        where TWork : OperationalWorkBase;

    /// <summary>
    /// Retention sweep: hard-deletes dead-lettered rows parked before <paramref name="cutoffUtc"/>, in bounded
    /// batches. Only dead-lettered rows are eligible - a live row is still owed a dispatch however old it is.
    /// </summary>
    Task<int> PurgeDeadLetteredAsync<TWork>(DateTimeOffset cutoffUtc, CancellationToken ct)
        where TWork : OperationalWorkBase;

    /// <summary>Backlog figures for the outbox health check.</summary>
    Task<OutboxBacklog> GetOutboxBacklogAsync(CancellationToken ct);
}
