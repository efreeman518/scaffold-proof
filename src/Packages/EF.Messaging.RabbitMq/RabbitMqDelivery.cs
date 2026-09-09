namespace EF.Messaging.RabbitMq;

/// <summary>A single delivery handed to an <see cref="IRabbitMqMessageHandler"/>.</summary>
/// <param name="Queue">Queue the delivery came from.</param>
/// <param name="RoutingKey">Routing key the publisher used.</param>
/// <param name="MessageId">AMQP <c>message-id</c> property, when the publisher set one.</param>
/// <param name="CorrelationId">AMQP <c>correlation-id</c> property.</param>
/// <param name="ContentType">AMQP <c>content-type</c> property.</param>
/// <param name="Headers">Application headers exactly as the broker delivered them; AMQP encodes strings as UTF-8 <c>byte[]</c>.</param>
/// <param name="Body">Payload bytes.</param>
/// <param name="DeliveryTag">Channel-scoped delivery tag used to acknowledge the message.</param>
/// <param name="Redelivered">True when the broker has delivered this message before.</param>
/// <param name="DeathCount">Sum of the <c>count</c> entries in the <c>x-death</c> header that name this queue.</param>
public sealed record RabbitMqDelivery(
    string Queue,
    string RoutingKey,
    string? MessageId,
    string? CorrelationId,
    string? ContentType,
    IReadOnlyDictionary<string, object?> Headers,
    ReadOnlyMemory<byte> Body,
    ulong DeliveryTag,
    bool Redelivered,
    int DeathCount);

/// <summary>What the consumer does with a delivery after the handler returns.</summary>
public enum ConsumeOutcome
{
    /// <summary>Acknowledge; the broker drops the message.</summary>
    Ack,

    /// <summary>Requeue for another attempt until the delivery bound is reached, then dead-letter.</summary>
    Retry,

    /// <summary>Dead-letter now, without another attempt.</summary>
    Reject
}

/// <summary>The handler's verdict for one delivery.</summary>
/// <param name="Outcome">What the consumer should do.</param>
/// <param name="Reason">Diagnostic reason, logged for <see cref="ConsumeOutcome.Retry"/> and <see cref="ConsumeOutcome.Reject"/>.</param>
public readonly record struct ConsumeResult(ConsumeOutcome Outcome, string? Reason = null)
{
    /// <summary>Acknowledge the delivery.</summary>
    public static ConsumeResult Ack => new(ConsumeOutcome.Ack);

    /// <summary>Requeue the delivery for another attempt.</summary>
    /// <param name="reason">Diagnostic reason.</param>
    public static ConsumeResult Retry(string reason) => new(ConsumeOutcome.Retry, reason);

    /// <summary>Dead-letter the delivery immediately.</summary>
    /// <param name="reason">Diagnostic reason.</param>
    public static ConsumeResult Reject(string reason) => new(ConsumeOutcome.Reject, reason);
}

/// <summary>Application logic for one queue. Resolved from a fresh scope for every delivery.</summary>
public interface IRabbitMqMessageHandler
{
    /// <summary>Handles one delivery.</summary>
    /// <param name="delivery">The delivery.</param>
    /// <param name="ct">Cancelled once the consumer's shutdown drain window has elapsed.</param>
    /// <returns>The verdict the consumer applies.</returns>
    Task<ConsumeResult> HandleAsync(RabbitMqDelivery delivery, CancellationToken ct);
}
