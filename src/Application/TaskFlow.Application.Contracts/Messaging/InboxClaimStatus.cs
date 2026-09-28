namespace TaskFlow.Application.Contracts.Messaging;

/// <summary>What <see cref="IInboxStore.TryClaimAsync"/> found for a (consumer, message) pair.</summary>
public enum InboxClaimStatus
{
    /// <summary>This delivery owns the claim and must run the effect, then complete or release it.</summary>
    Acquired,

    /// <summary>The effect already ran; the delivery is settled without running it again.</summary>
    Duplicate,

    /// <summary>Another delivery holds a live claim; this one waits for it to resolve or is retried, never acknowledged.</summary>
    InProgress
}
