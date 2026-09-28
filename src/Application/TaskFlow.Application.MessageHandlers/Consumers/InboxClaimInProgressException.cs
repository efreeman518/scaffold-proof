namespace TaskFlow.Application.MessageHandlers.Consumers;

/// <summary>
/// Thrown when another delivery of the same message holds a live inbox claim. It is a transient failure by
/// design: both transports retry a thrown delivery (Service Bus abandons it, RabbitMQ requeues it), and by the
/// time it comes back the other delivery has either completed (duplicate) or its lease has expired (takeover).
/// Acknowledging instead would lose the effect if the other delivery then failed.
/// </summary>
/// <param name="consumer">Consumer whose claim is held.</param>
/// <param name="messageId">Message being processed by the other delivery.</param>
public sealed class InboxClaimInProgressException(string consumer, Guid messageId)
    : Exception($"Consumer {consumer} is already processing message {messageId}; retry later.")
{
    /// <summary>Consumer whose claim is held.</summary>
    public string Consumer { get; } = consumer;

    /// <summary>Message being processed by the other delivery.</summary>
    public Guid MessageId { get; } = messageId;
}
