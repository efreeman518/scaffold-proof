namespace TaskFlow.Application.Contracts.Messaging;

/// <summary>
/// D-029 consumer idempotency. Insert-first inside the consumer's unit of work: the first claim of a
/// (consumer, messageId) pair wins, every later delivery of the same message returns false and is skipped.
/// </summary>
public interface IInboxStore
{
    /// <summary>True when this call inserted the inbox row (first delivery); false when it already existed.</summary>
    Task<bool> TryClaimAsync(string consumer, Guid messageId, CancellationToken ct = default);
}
