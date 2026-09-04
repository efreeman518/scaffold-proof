using Microsoft.Extensions.Logging;
using TaskFlow.Observability;

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

    /// <summary>Logs a failure inside a leased worker poll; the loop backs off and retries.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 6, Level = LogLevel.Error, Message = "Leased worker poll for {WorkType} failed")]
    public static partial void LeasedWorkerPollFailed(this ILogger logger, string workType, Exception exception);

    /// <summary>Logs that the outbox dispatcher did not start because no broker is configured.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 7, Level = LogLevel.Warning, Message = "No messaging transport configured: outbox dispatcher not started, staged rows remain pending")]
    public static partial void OutboxDispatcherDisabled(this ILogger logger);

    /// <summary>Logs that one destination batch failed and its rows were released.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 8, Level = LogLevel.Warning, Message = "Outbox dispatch to {Destination} failed for {Count} message(s); rows released for retry")]
    public static partial void OutboxDispatchFailed(this ILogger logger, string destination, int count, Exception exception);

    /// <summary>Logs that blob-delete work was claimed with no blob storage configured.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 9, Level = LogLevel.Warning, Message = "Blob storage is not configured: {Count} blob-delete row(s) left pending")]
    public static partial void BlobDeleteWorkerDisabled(this ILogger logger, int count);

    /// <summary>Logs a blob delete that failed and will be retried.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 10, Level = LogLevel.Warning, Message = "Deferred delete of {Container}/{BlobName} failed; row released for retry")]
    public static partial void BlobDeleteFailed(this ILogger logger, string container, string blobName, Exception exception);

    /// <summary>Logs a recurrence template the generator cannot advance; its schedule pointer is cleared.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 11, Level = LogLevel.Warning, Message = "Recurrence template {TemplateId} has unusable frequency '{Frequency}'; removed from the due set until its pattern is re-saved")]
    public static partial void RecurrenceTemplateUnusable(this ILogger logger, Guid templateId, string frequency);

    /// <summary>Logs a generated occurrence rejected by domain validation.</summary>
    [LoggerMessage(EventId = LogEventIds.SchedulerBase + 12, Level = LogLevel.Error, Message = "Occurrence {OccurrenceUtc} of template {TemplateId} was rejected: {Reason}")]
    public static partial void RecurrenceOccurrenceRejected(this ILogger logger, Guid templateId, DateTimeOffset occurrenceUtc, string reason);
}
