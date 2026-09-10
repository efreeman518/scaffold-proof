using Microsoft.Extensions.Logging;
using TaskFlow.Observability;

namespace TaskFlow.Infrastructure.Storage;

/// <summary>
/// Source-generated logging methods for the Infrastructure.Storage layer. Using <see cref="LoggerMessageAttribute"/>
/// defers argument evaluation until the log level is enabled, satisfying CA1873 and avoiding needless work.
/// </summary>
internal static partial class LogMessages
{
    /// <summary>Logs that an audit entry was persisted to the audit store.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureStorageBase + 1, Level = LogLevel.Information, Message = "Persisted audit entry {AuditEntryId} for tenant {TenantId} entity {EntityType} action {Action}")]
    public static partial void AuditEntryPersisted(this ILogger logger, Guid auditEntryId, string? tenantId, string entityType, string action);

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

    /// <summary>Logs the audit rows removed by one retention sweep.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureStorageBase + 10, Level = LogLevel.Information, Message = "Purged {Count} audit entries recorded before {CutoffUtc}")]
    public static partial void AuditEntriesPurged(this ILogger logger, int count, DateTimeOffset cutoffUtc);

    /// <summary>Logs once that no object-storage backend is configured, so IObjectStorageRepository is the no-op.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureStorageBase + 11, Level = LogLevel.Warning, Message = "No object-storage backend configured: attachment upload, download, and URL generation will fail; deferred blob deletes will be treated as already gone.")]
    public static partial void NoOpBlobStorageConfigured(this ILogger logger);
}
