namespace TaskFlow.Application.Contracts.Messaging;

/// <summary>
/// D-029 consumer idempotency as a two-state claim. A delivery first takes an in-progress claim with a lease,
/// runs its effect, then marks the claim completed. A redelivery that finds a completed claim is a duplicate; one
/// that finds a live in-progress claim must retry later; one that finds an expired in-progress claim (the
/// earlier delivery crashed or hung) takes it over. Committing the claim only as "done" after the work is what
/// stops a crash or a concurrent redelivery from swallowing the effect.
/// </summary>
public interface IInboxStore
{
    /// <summary>
    /// Takes the claim for one (consumer, message) pair, or reports why it cannot. Runs immediately on the
    /// store's connection, joining the caller's transaction when one is open.
    /// </summary>
    /// <param name="consumer">Consumer name.</param>
    /// <param name="messageId">Broker message id (the outbox row id).</param>
    /// <param name="leaseDuration">
    /// How long the claim stays in progress before another delivery may take it over. Must exceed the longest
    /// handler run and, on Service Bus, the message lock renewal window.
    /// </param>
    /// <param name="ct">Cancellation token.</param>
    Task<InboxClaim> TryClaimAsync(string consumer, Guid messageId, TimeSpan leaseDuration, CancellationToken ct = default);

    /// <summary>
    /// Marks an acquired claim completed after its effect ran. False when the claim was taken over after its
    /// lease expired, in which case the effect may have run twice.
    /// </summary>
    Task<bool> CompleteAsync(string consumer, Guid messageId, Guid claimToken, CancellationToken ct = default);

    /// <summary>
    /// Removes an acquired, still in-progress claim whose work failed, so the redelivery runs at once instead of
    /// waiting out the lease. False when the claim was already taken over or completed.
    /// </summary>
    Task<bool> ReleaseAsync(string consumer, Guid messageId, Guid claimToken, CancellationToken ct = default);

    /// <summary>
    /// Retention sweep: hard-deletes claims completed before <paramref name="cutoffUtc"/> and abandoned in-progress
    /// claims whose lease expired before it. The window has to outlive the broker's maximum redelivery age, or a
    /// late redelivery would find no claim and run its effect twice.
    /// </summary>
    Task<int> PurgeAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default);
}
