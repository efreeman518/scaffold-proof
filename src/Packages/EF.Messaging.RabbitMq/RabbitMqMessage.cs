namespace EF.Messaging.RabbitMq;

/// <summary>
/// A message to publish. The payload is opaque bytes; serialization belongs to the caller.
/// </summary>
/// <param name="Body">Payload bytes.</param>
/// <param name="RoutingKey">Routing key used by the target exchange.</param>
/// <param name="MessageId">Application message id, set as the AMQP <c>message-id</c> property.</param>
/// <param name="ContentType">AMQP <c>content-type</c> property.</param>
/// <param name="CorrelationId">AMQP <c>correlation-id</c> property.</param>
/// <param name="Headers">
/// Application headers. Supported value types are <see cref="string"/>, <see cref="int"/>, <see cref="long"/>,
/// <see cref="bool"/>, <c>byte[]</c> and null; any other type throws <see cref="ArgumentException"/> before
/// anything is published. String values arrive at a consumer as UTF-8 <c>byte[]</c>, which is how AMQP encodes them.
/// </param>
/// <param name="Persistent">Sets delivery mode 2 so the broker persists the message on a durable queue.</param>
public sealed record RabbitMqMessage(
    ReadOnlyMemory<byte> Body,
    string RoutingKey,
    string MessageId,
    string ContentType = "application/json",
    string? CorrelationId = null,
    IReadOnlyDictionary<string, object?>? Headers = null,
    bool Persistent = true);

/// <summary>Publishes messages with publisher confirms.</summary>
public interface IRabbitMqPublisher
{
    /// <summary>
    /// Publishes every message on one pooled channel and awaits the broker confirms for the whole batch within
    /// <see cref="RabbitMqOptions.PublisherConfirmTimeout"/>.
    /// </summary>
    /// <param name="exchange">Target exchange; the empty string publishes to the default exchange.</param>
    /// <param name="messages">Messages to publish, in order.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">A header value has an unsupported type. Nothing is published.</exception>
    /// <exception cref="RabbitMqPublishException">One or more messages were nacked, returned or not confirmed in time.</exception>
    Task PublishBatchAsync(string exchange, IReadOnlyList<RabbitMqMessage> messages, CancellationToken ct = default);

    /// <summary>Publishes a single message; equivalent to a one-element <see cref="PublishBatchAsync"/>.</summary>
    /// <param name="exchange">Target exchange.</param>
    /// <param name="message">Message to publish.</param>
    /// <param name="ct">Cancellation token.</param>
    Task PublishAsync(string exchange, RabbitMqMessage message, CancellationToken ct = default);
}

/// <summary>Thrown when a publish batch is not fully confirmed.</summary>
public sealed class RabbitMqPublishException : Exception
{
    /// <summary>Creates the exception for the given failed batch positions.</summary>
    /// <param name="message">Diagnostic message.</param>
    /// <param name="unconfirmedIndices">Zero-based indices into the published batch that were not confirmed.</param>
    /// <param name="innerException">The first underlying failure, when there was one.</param>
    public RabbitMqPublishException(string message, IReadOnlyList<int> unconfirmedIndices, Exception? innerException = null)
        : base(message, innerException) => UnconfirmedIndices = unconfirmedIndices;

    /// <summary>Zero-based indices into the batch passed to <see cref="IRabbitMqPublisher.PublishBatchAsync"/>.</summary>
    public IReadOnlyList<int> UnconfirmedIndices { get; }
}
