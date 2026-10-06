using Microsoft.Extensions.Logging;
using TaskFlow.Observability;

namespace TaskFlow.Scheduler;

/// <summary>
/// Source-generated logging methods for the Scheduler host. Using <see cref="LoggerMessageAttribute"/>
/// defers argument evaluation until the log level is enabled, satisfying CA1873 and avoiding needless work.
/// </summary>
internal static partial class LogMessages
{
    /// <summary>Logs the number of overdue tasks found.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 3, Level = LogLevel.Information, Message = "Found {Count} overdue tasks")]
    public static partial void OverdueTasksFound(this ILogger logger, int count);

    /// <summary>Logs the number of recurring task templates found.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 4, Level = LogLevel.Information, Message = "Found {Count} recurring task templates to evaluate")]
    public static partial void RecurringTemplatesFound(this ILogger logger, int count);

    /// <summary>Logs the number of stale tasks found.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 5, Level = LogLevel.Information, Message = "Found {Count} stale tasks (cancelled > {StaleDays} days ago)")]
    public static partial void StaleTasksFound(this ILogger logger, int count, int staleDays);

    // EventId SchedulerBase + 6 was LeasedWorkerPollFailed; EF.BackgroundServices.LeasedWorkerBase logs the
    // failed batch itself (package request 11). Not reused - a shipped EventId is retired, never renumbered.

    /// <summary>Logs a blob delete that failed and will be retried.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 10, Level = LogLevel.Warning, Message = "Deferred delete of {Container}/{BlobName} failed; row released for retry")]
    public static partial void BlobDeleteFailed(this ILogger logger, string container, string blobName, Exception exception);

    /// <summary>Logs a recurrence template the generator cannot advance; its schedule pointer is cleared.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 11, Level = LogLevel.Warning, Message = "Recurrence template {TemplateId} has unusable frequency '{Frequency}'; removed from the due set until its pattern is re-saved")]
    public static partial void RecurrenceTemplateUnusable(this ILogger logger, Guid templateId, string frequency);

    /// <summary>Logs a generated occurrence rejected by domain validation.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 12, Level = LogLevel.Error, Message = "Occurrence {OccurrenceUtc} of template {TemplateId} was rejected: {Reason}")]
    public static partial void RecurrenceOccurrenceRejected(this ILogger logger, Guid templateId, DateTimeOffset occurrenceUtc, string reason);

    /// <summary>Logs that TickerQ is running without a persisted operational store.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 16, Level = LogLevel.Information, Message = "TickerQ running without persisted operational store.")]
    public static partial void TickerQPersistenceDisabled(this ILogger logger);

    /// <summary>Logs that the TickerQ operational-store schema was validated.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 17, Level = LogLevel.Information, Message = "TickerQ operational-store schema validated.")]
    public static partial void TickerQSchemaValidated(this ILogger logger);

    /// <summary>Logs the compliance-check instance a tenant's start resolved to (new, or the day's existing one).</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 20, Level = LogLevel.Information, Message = "Compliance check for tenant {TenantId} is instance {InstanceId} ({Status})")]
    public static partial void ComplianceCheckStarted(this ILogger logger, Guid tenantId, string instanceId, EF.FlowEngine.Model.ExecStatus status);

    /// <summary>Logs how many tenants the compliance-check run started.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 21, Level = LogLevel.Information, Message = "Compliance check started for {Started} of {Tenants} tenants with a due compliance task")]
    public static partial void ComplianceCheckTenantsStarted(this ILogger logger, int started, int tenants);

    // EventIds SchedulerBase + 18 and + 19 were TickerQCronManagerUnavailable / TickerQCronJobsSeeded. The cron
    // expressions now live on [TickerFunction] and TickerQ seeds them itself; retired, never reused.
}
