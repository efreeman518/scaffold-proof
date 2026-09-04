using Microsoft.Extensions.Logging;
using TaskFlow.Observability;

namespace TaskFlow.Application.MessageHandlers;

/// <summary>
/// Source-generated logging methods for the Application.MessageHandlers layer. Using
/// <see cref="LoggerMessageAttribute"/> defers argument evaluation until the log level is enabled,
/// satisfying CA1873 and avoiding needless work.
/// </summary>
internal static partial class LogMessages
{
    /// <summary>Logs that an audit message was persisted.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationMessageHandlersBase + 1, Level = LogLevel.Debug, Message = "Persisted audit message {AuditEntryId} for {EntityType} {Action}")]
    public static partial void AuditMessagePersisted(this ILogger logger, Guid auditEntryId, string entityType, string action);

    /// <summary>Logs that a workflow was started for a task item.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationMessageHandlersBase + 2, Level = LogLevel.Information, Message = "Started workflow {WorkflowId} instance {InstanceId} for TaskItem {TaskId}")]
    public static partial void WorkflowStarted(this ILogger logger, string workflowId, string instanceId, Guid taskId);

    /// <summary>Logs that a redelivery was skipped because the consumer inbox already had the message.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationMessageHandlersBase + 3, Level = LogLevel.Debug, Message = "Consumer {Consumer} skipped duplicate {EventType} {MessageId}")]
    public static partial void ConsumerDuplicateSkipped(this ILogger logger, string consumer, string eventType, Guid messageId);
}
