using Microsoft.Extensions.Logging;
using TaskFlow.Observability;

namespace TaskFlow.Infrastructure.Storage;

/// <summary>
/// Source-generated logging methods for the Infrastructure.Storage layer. Using <see cref="LoggerMessageAttribute"/>
/// defers argument evaluation until the log level is enabled, satisfying CA1873 and avoiding needless work.
/// </summary>
internal static partial class LogMessages
{
    /// <summary>Logs that a TaskView read model was upserted.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureStorageBase + 2, Level = LogLevel.Debug, Message = "Upserted TaskView {Id} for tenant {TenantId}")]
    public static partial void TaskViewUpserted(this ILogger logger, string id, string tenantId);

    /// <summary>Logs that a TaskView was not there to patch; the create projection will build it.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureStorageBase + 9, Level = LogLevel.Debug, Message = "TaskView {Id} not found for counter patch")]
    public static partial void TaskViewNotFoundForPatch(this ILogger logger, string id);

    /// <summary>Logs that a TaskView was not found during deletion.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureStorageBase + 3, Level = LogLevel.Debug, Message = "TaskView {Id} not found for deletion")]
    public static partial void TaskViewNotFoundForDeletion(this ILogger logger, string id);

    /// <summary>Logs a no-op TaskView upsert.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureStorageBase + 4, Level = LogLevel.Debug, Message = "NoOp: Would upsert TaskView {Id}")]
    public static partial void NoOpTaskViewUpsert(this ILogger logger, string id);

    /// <summary>Logs a no-op audit entry persist.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureStorageBase + 5, Level = LogLevel.Debug, Message = "NoOp: would persist audit entry {AuditEntryId}")]
    public static partial void NoOpAuditPersist(this ILogger logger, Guid auditEntryId);

    /// <summary>Logs that no broker is configured, so outbox rows stay in the table.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureStorageBase + 6, Level = LogLevel.Warning, Message = "No messaging transport configured: {Count} outbox row(s) for {Destination} are left pending")]
    public static partial void NoOpTransport(this ILogger logger, string destination, int count);

    /// <summary>Logs that one claimed outbox batch was handed to the broker.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureStorageBase + 8, Level = LogLevel.Debug, Message = "Sent {Count} outbox message(s) to {Destination}")]
    public static partial void OutboxBatchSent(this ILogger logger, string destination, int count);

    /// <summary>Logs once that no object-storage backend is configured, so IObjectStorageRepository is the no-op.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureStorageBase + 11, Level = LogLevel.Warning, Message = "No object-storage backend configured: attachment upload, download, and URL generation will fail; deferred blob deletes will be treated as already gone.")]
    public static partial void NoOpBlobStorageConfigured(this ILogger logger);

    /// <summary>Logs an outbox message that can never fit a Service Bus batch; it is reported permanent and dead-lettered.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureStorageBase + 12, Level = LogLevel.Error, Message = "Outbox message {MessageId} ({EventType}) to {Destination} exceeds the Service Bus batch limit of {MaxSizeInBytes} bytes; it will be dead-lettered")]
    public static partial void OutboxMessageTooLarge(this ILogger logger, Guid messageId, string eventType, string destination, long maxSizeInBytes);

    /// <summary>Logs a Service Bus send that failed part way; earlier batches stayed sent, the rest is retried.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureStorageBase + 13, Level = LogLevel.Warning, Message = "Service Bus send to {Destination} failed after {SentCount} message(s); {FailedCount} message(s) will be retried")]
    public static partial void OutboxBatchSendFailed(this ILogger logger, string destination, int sentCount, int failedCount, Exception exception);
}
