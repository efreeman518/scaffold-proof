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

    /// <summary>Logs that the embedding row for a task that no longer exists was removed.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationMessageHandlersBase + 4, Level = LogLevel.Information, Message = "Removed the embedding for TaskItem {TaskId} in tenant {TenantId}: the task no longer exists")]
    public static partial void TaskEmbeddingRemovedForMissingTask(this ILogger logger, Guid taskId, Guid tenantId);

    /// <summary>Logs that a task was re-embedded.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationMessageHandlersBase + 5, Level = LogLevel.Debug, Message = "Embedded TaskItem {TaskId} with {ModelId} ({Dimensions} dimensions)")]
    public static partial void TaskEmbeddingUpserted(this ILogger logger, Guid taskId, string modelId, int dimensions);
}
