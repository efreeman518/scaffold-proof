using Microsoft.Extensions.Logging;

namespace EF.Messaging.RabbitMq;

/// <summary>
/// Source-generated logging for the RabbitMQ transport. <see cref="LoggerMessageAttribute"/> defers argument
/// evaluation until the level is enabled. Event ids are package-local and start at 7000.
/// </summary>
internal static partial class LogMessages
{
    private const int Base = 7000;

    [LoggerMessage(EventId = Base + 1, Level = LogLevel.Information, Message = "RabbitMQ connection {ClientProvidedName} established to {Endpoint}")]
    public static partial void ConnectionEstablished(this ILogger logger, string clientProvidedName, string endpoint);

    [LoggerMessage(EventId = Base + 2, Level = LogLevel.Information, Message = "RabbitMQ using the IConnection already registered in the container; RabbitMqOptions.ConnectionString ignored")]
    public static partial void ExternalConnectionUsed(this ILogger logger);

    [LoggerMessage(EventId = Base + 3, Level = LogLevel.Warning, Message = "RabbitMQ connection shut down: {ReplyCode} {ReplyText}")]
    public static partial void ConnectionShutdown(this ILogger logger, ushort replyCode, string? replyText);

    [LoggerMessage(EventId = Base + 4, Level = LogLevel.Warning, Message = "Discarding publisher channel {ChannelNumber} closed by the broker: {CloseReason}")]
    public static partial void PublisherChannelDiscarded(this ILogger logger, int channelNumber, string? closeReason);

    [LoggerMessage(EventId = Base + 5, Level = LogLevel.Error, Message = "Publish batch to exchange {Exchange} left {UnconfirmedCount} of {MessageCount} messages unconfirmed; first unconfirmed MessageId {MessageId}")]
    public static partial void PublishBatchUnconfirmed(this ILogger logger, string exchange, int unconfirmedCount, int messageCount, string? messageId, Exception? exception);

    [LoggerMessage(EventId = Base + 6, Level = LogLevel.Information, Message = "Declared RabbitMQ topology: {ExchangeCount} exchanges, {QueueCount} queues, {BindingCount} bindings")]
    public static partial void TopologyDeclared(this ILogger logger, int exchangeCount, int queueCount, int bindingCount);

    [LoggerMessage(EventId = Base + 7, Level = LogLevel.Information, Message = "Consuming queue {Queue} with prefetch {PrefetchCount} and max delivery count {MaxDeliveryCount}")]
    public static partial void ConsumerStarted(this ILogger logger, string queue, ushort prefetchCount, int maxDeliveryCount);

    [LoggerMessage(EventId = Base + 8, Level = LogLevel.Information, Message = "Consumer for queue {Queue} is disabled by configuration; not starting")]
    public static partial void ConsumerDisabled(this ILogger logger, string queue);

    [LoggerMessage(EventId = Base + 9, Level = LogLevel.Information, Message = "Consumer for queue {Queue} stopped with {InFlight} deliveries still in flight")]
    public static partial void ConsumerStopped(this ILogger logger, string queue, int inFlight);

    [LoggerMessage(EventId = Base + 10, Level = LogLevel.Warning, Message = "Handler failed for queue {Queue} delivery {DeliveryTag} MessageId {MessageId}; treating as retry")]
    public static partial void HandlerFailed(this ILogger logger, string queue, ulong deliveryTag, string? messageId, Exception exception);

    [LoggerMessage(EventId = Base + 11, Level = LogLevel.Warning, Message = "Requeueing queue {Queue} delivery {DeliveryTag} MessageId {MessageId} after attempt {Attempt} of {MaxDeliveryCount}: {Reason}")]
    public static partial void DeliveryRequeued(this ILogger logger, string queue, ulong deliveryTag, string? messageId, int attempt, int maxDeliveryCount, string? reason);

    [LoggerMessage(EventId = Base + 12, Level = LogLevel.Warning, Message = "Dead-lettering queue {Queue} delivery {DeliveryTag} MessageId {MessageId} after attempt {Attempt}: {Reason}")]
    public static partial void DeliveryDeadLettered(this ILogger logger, string queue, ulong deliveryTag, string? messageId, int attempt, string? reason);

    [LoggerMessage(EventId = Base + 13, Level = LogLevel.Warning, Message = "Shutdown drain for queue {Queue} ended with {InFlight} deliveries still in flight; they will be redelivered")]
    public static partial void ShutdownDrainIncomplete(this ILogger logger, string queue, int inFlight);

    [LoggerMessage(EventId = Base + 14, Level = LogLevel.Error, Message = "Failed to settle queue {Queue} delivery {DeliveryTag} MessageId {MessageId} as {Outcome}; the broker will redeliver it")]
    public static partial void SettleFailed(this ILogger logger, string queue, ulong deliveryTag, string? messageId, ConsumeOutcome outcome, Exception exception);

    [LoggerMessage(EventId = Base + 15, Level = LogLevel.Warning, Message = "Queue {Queue} delivery {DeliveryTag} has no MessageId; the retry bound falls back to the x-death count only")]
    public static partial void DeliveryWithoutMessageId(this ILogger logger, string queue, ulong deliveryTag);
}
