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

    /// <summary>Logs a delivery sent back for retry because another delivery still held a live inbox claim after the wait.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationMessageHandlersBase + 6, Level = LogLevel.Warning, Message = "Consumer {Consumer} waited a full claim lease on {EventType} {MessageId}, still in progress under another delivery; retrying later")]
    public static partial void ConsumerClaimInProgress(this ILogger logger, string consumer, string eventType, Guid messageId);

    /// <summary>Logs a completed consume whose inbox claim had been taken over after its lease expired.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationMessageHandlersBase + 7, Level = LogLevel.Warning, Message = "Consumer {Consumer} finished {EventType} {MessageId} after its inbox claim was taken over; the effect may have run twice")]
    public static partial void ConsumerClaimLost(this ILogger logger, string consumer, string eventType, Guid messageId);

    /// <summary>Logs a failed consume whose inbox claim could not be released; the lease expiry recovers it.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationMessageHandlersBase + 8, Level = LogLevel.Error, Message = "Consumer {Consumer} failed {EventType} {MessageId} and could not release its inbox claim; a redelivery takes it over when the lease expires")]
    public static partial void ConsumerReleaseFailed(this ILogger logger, Exception exception, string consumer, string eventType, Guid messageId);

    /// <summary>Logs a renewal that found the claim gone: another delivery took it over, so renewal stops.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationMessageHandlersBase + 9, Level = LogLevel.Warning, Message = "Consumer {Consumer} lost its inbox claim on {EventType} {MessageId} while still running; the effect may run twice")]
    public static partial void ConsumerClaimRenewalLost(this ILogger logger, string consumer, string eventType, Guid messageId);

    /// <summary>Logs a renewal that failed; the handler keeps going and the next renewal retries.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationMessageHandlersBase + 10, Level = LogLevel.Warning, Message = "Consumer {Consumer} could not renew its inbox claim on {EventType} {MessageId}; retrying at the next renewal")]
    public static partial void ConsumerClaimRenewalFailed(this ILogger logger, Exception exception, string consumer, string eventType, Guid messageId);
}
