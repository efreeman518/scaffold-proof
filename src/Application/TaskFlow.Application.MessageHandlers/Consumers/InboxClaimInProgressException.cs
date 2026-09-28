namespace TaskFlow.Application.MessageHandlers.Consumers;

/// <summary>
/// Thrown when another delivery of the same message still holds a live, renewed inbox claim after this delivery
/// waited one full claim lease plus a margin for it (<see cref="InboxClaimOptions.WaitBound"/>). The holder is
/// alive and working, so this is a transient failure by design: both transports retry a thrown delivery (Service
/// Bus abandons it, RabbitMQ requeues it), and by then the holder has completed (duplicate) or failed and released
/// the claim. Acknowledging instead would lose the effect if the holder then failed. A crashed holder never gets
/// here: it stops renewing, and the waiting delivery takes its claim over within one lease.
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
