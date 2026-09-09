using Microsoft.Extensions.Logging;
using TaskFlow.Observability;

namespace TaskFlow.Infrastructure.Messaging.RabbitMq;

/// <summary>
/// Source-generated logging methods for the RabbitMQ consumer handlers. Using
/// <see cref="LoggerMessageAttribute"/> defers argument evaluation until the log level is enabled,
/// satisfying CA1873 and avoiding needless work. Topology-related events live in
/// <see cref="RabbitMqTopologyLog"/> (<c>TaskFlowRabbitMqTopologyStartup.cs</c>).
/// </summary>
internal static partial class LogMessages
{
    /// <summary>Logs a delivery rejected because its envelope could not be read.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureMessagingRabbitMqBase + 3, Level = LogLevel.Warning, Message = "Consumer {Consumer} rejected message {MessageId} from {Queue}: {Reason}")]
    public static partial void ConsumerMessageRejected(this ILogger logger, string consumer, string? messageId, string queue, string reason);
}
