using Microsoft.Extensions.Logging;
using TaskFlow.Observability;
using TickerQ.Utilities.Enums;

namespace TaskFlow.Scheduler;

/// <summary>
/// Source-generated logging methods for the Scheduler host. Using <see cref="LoggerMessageAttribute"/>
/// defers argument evaluation until the log level is enabled, satisfying CA1873 and avoiding needless work.
/// </summary>
internal static partial class LogMessages
{
    /// <summary>Logs that a scheduled job is starting.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 1, Level = LogLevel.Information, Message = "Job {JobName} starting at {UtcNow}")]
    public static partial void JobStarting(this ILogger logger, string jobName, DateTime utcNow);

    /// <summary>Logs that a scheduled job completed.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 2, Level = LogLevel.Information, Message = "Job {JobName} completed in {ElapsedMs}ms")]
    public static partial void JobCompleted(this ILogger logger, string jobName, long elapsedMs);

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

    /// <summary>Logs that the outbox dispatcher did not start because no broker is configured.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 7, Level = LogLevel.Warning, Message = "No messaging transport configured: outbox dispatcher not started, staged rows remain pending")]
    public static partial void OutboxDispatcherDisabled(this ILogger logger);

    /// <summary>Logs that one destination batch failed and its rows were released.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 8, Level = LogLevel.Warning, Message = "Outbox dispatch to {Destination} failed for {Count} message(s); rows released for retry")]
    public static partial void OutboxDispatchFailed(this ILogger logger, string destination, int count, Exception exception);

    /// <summary>Logs a blob delete that failed and will be retried.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 10, Level = LogLevel.Warning, Message = "Deferred delete of {Container}/{BlobName} failed; row released for retry")]
    public static partial void BlobDeleteFailed(this ILogger logger, string container, string blobName, Exception exception);

    /// <summary>Logs a recurrence template the generator cannot advance; its schedule pointer is cleared.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 11, Level = LogLevel.Warning, Message = "Recurrence template {TemplateId} has unusable frequency '{Frequency}'; removed from the due set until its pattern is re-saved")]
    public static partial void RecurrenceTemplateUnusable(this ILogger logger, Guid templateId, string frequency);

    /// <summary>Logs a generated occurrence rejected by domain validation.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 12, Level = LogLevel.Error, Message = "Occurrence {OccurrenceUtc} of template {TemplateId} was rejected: {Reason}")]
    public static partial void RecurrenceOccurrenceRejected(this ILogger logger, Guid templateId, DateTimeOffset occurrenceUtc, string reason);

    /// <summary>Logs an unhandled TickerQ job failure (ITickerExceptionHandler.HandleExceptionAsync).</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 13, Level = LogLevel.Error, Message = "Scheduler job failed. JobName: {JobName}, TickerId: {TickerId}, TickerType: {TickerType}")]
    public static partial void SchedulerJobFailed(this ILogger logger, Exception exception, string jobName, Guid tickerId, TickerType tickerType);

    /// <summary>Logs a cancelled TickerQ job (ITickerExceptionHandler.HandleCanceledExceptionAsync).</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 14, Level = LogLevel.Warning, Message = "Scheduler job cancelled. JobName: {JobName}, TickerId: {TickerId}, TickerType: {TickerType}, Reason: {Reason}")]
    public static partial void SchedulerJobCancelled(this ILogger logger, string jobName, Guid tickerId, TickerType tickerType, string reason);

    /// <summary>Logs a job failure caught by the shared TickerQ job wrapper before it rethrows for TickerQ's retry policy.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 15, Level = LogLevel.Error, Message = "Job {JobName} failed after {ElapsedMs}ms")]
    public static partial void TickerQJobExecutionFailed(this ILogger logger, Exception exception, string jobName, long elapsedMs);

    /// <summary>Logs that TickerQ is running without a persisted operational store.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 16, Level = LogLevel.Information, Message = "TickerQ running without persisted operational store.")]
    public static partial void TickerQPersistenceDisabled(this ILogger logger);

    /// <summary>Logs that the TickerQ operational-store schema was validated.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 17, Level = LogLevel.Information, Message = "TickerQ operational-store schema validated.")]
    public static partial void TickerQSchemaValidated(this ILogger logger);

    // EventIds SchedulerBase + 18 and + 19 were TickerQCronManagerUnavailable / TickerQCronJobsSeeded. The cron
    // expressions now live on [TickerFunction] and TickerQ seeds them itself; retired, never reused.

    /// <summary>Logs a work-table handler that threw; the rows it did not report are released for retry.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 20, Level = LogLevel.Error, Message = "{WorkType} handler failed on a batch of {Count}; unreported rows are released for retry")]
    public static partial void WorkBatchHandlerFailed(this ILogger logger, string workType, int count, Exception exception);

    /// <summary>Logs a work row parked after a permanent failure or its last attempt.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 21, Level = LogLevel.Warning, Message = "{WorkType} row {Id} dead-lettered after {Attempts} attempt(s): {Error}")]
    public static partial void WorkItemDeadLettered(this ILogger logger, string workType, Guid id, int attempts, string error);

    /// <summary>Logs settlement statements that changed no row because another replica took the lease over.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 22, Level = LogLevel.Warning, Message = "{WorkType}: lease {LeaseToken} was lost for {Count} row(s) before settlement; another replica owns them")]
    public static partial void WorkLeaseLost(this ILogger logger, string workType, int count, Guid leaseToken);

    /// <summary>Logs the first failed Azure App Configuration refresh of a failure streak.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 23, Level = LogLevel.Warning, Message = "Azure App Configuration refresh failed; the Scheduler keeps its last configuration and flags until a refresh succeeds")]
    public static partial void AppConfigurationRefreshFailed(this ILogger logger);

    /// <summary>Logs the first successful Azure App Configuration refresh after a failure streak.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 24, Level = LogLevel.Information, Message = "Azure App Configuration refresh recovered after {FailedRefreshes} failed attempt(s)")]
    public static partial void AppConfigurationRefreshRecovered(this ILogger logger, int failedRefreshes);
}
