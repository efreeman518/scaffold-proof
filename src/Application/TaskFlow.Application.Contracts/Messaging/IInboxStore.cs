namespace TaskFlow.Application.Contracts.Messaging;

/// <summary>
/// D-029 consumer idempotency. Insert-first inside the consumer's unit of work: the first claim of a
/// (consumer, messageId) pair wins, every later delivery of the same message returns false and is skipped.
/// </summary>
public interface IInboxStore
{
    /// <summary>True when this call inserted the inbox row (first delivery); false when it already existed.</summary>
    Task<bool> TryClaimAsync(string consumer, Guid messageId, CancellationToken ct = default);

    /// <summary>
    /// Removes a claim whose work then failed, so the redelivery is processed instead of skipped. Without this
    /// compensation a claim taken before the work would swallow the retry and lose the effect.
    /// </summary>
    Task ReleaseAsync(string consumer, Guid messageId, CancellationToken ct = default);

    /// <summary>
    /// Retention sweep: hard-deletes claims processed before <paramref name="cutoffUtc"/>. The window has to
    /// outlive the broker's maximum redelivery age, or a late redelivery would find no claim and be processed twice.
    /// </summary>
    Task<int> PurgeProcessedAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default);
}
