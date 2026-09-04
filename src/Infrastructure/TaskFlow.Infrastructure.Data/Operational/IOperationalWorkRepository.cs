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
public sealed record OutboxBacklog(int Pending, int DeadLettered, TimeSpan Lag);

/// <summary>
/// Provider-neutral lease claim over the operational work tables (D-026). No provider branch: the single-statement
/// upgrades are documented in the implementation as comments.
/// </summary>
public interface IOperationalWorkRepository
{
    /// <summary>Claims up to <paramref name="batchSize"/> due rows whose lease is free or expired.</summary>
    Task<LeasedBatch<TWork>> ClaimAsync<TWork>(int batchSize, TimeSpan leaseDuration, string owner, CancellationToken ct)
        where TWork : OperationalWorkBase;

    /// <summary>Hard-deletes the rows that were handled successfully; the lease token guards against a stolen lease.</summary>
    Task<int> CompleteAsync<TWork>(Guid leaseToken, IReadOnlyCollection<Guid> ids, CancellationToken ct)
        where TWork : OperationalWorkBase;

    /// <summary>
    /// Releases one row after a transient failure with an exponential backoff, or dead-letters it once the
    /// attempt ceiling is reached. A dead-lettered row is kept: it is the only surviving copy of the event.
    /// </summary>
    Task ReleaseAsync<TWork>(Guid leaseToken, Guid id, int attemptCount, string error, CancellationToken ct)
        where TWork : OperationalWorkBase;

    /// <summary>Resets one dead-lettered row so the dispatcher picks it up again. False when it was not dead-lettered.</summary>
    Task<bool> RetryDeadLetteredAsync<TWork>(Guid id, CancellationToken ct)
        where TWork : OperationalWorkBase;

    /// <summary>Backlog figures for the outbox health check.</summary>
    Task<OutboxBacklog> GetOutboxBacklogAsync(CancellationToken ct);
}
