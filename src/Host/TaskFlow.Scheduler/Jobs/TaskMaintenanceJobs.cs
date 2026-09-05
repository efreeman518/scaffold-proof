using TaskFlow.Scheduler.Handlers;
using TaskFlow.Scheduler.Handlers.Retention;
using TaskFlow.Scheduler.Telemetry;
using TickerQ.Utilities.Base;

namespace TaskFlow.Scheduler.Jobs;

/// <summary>Configures task maintenance jobs host behavior for TaskFlow runtime services.</summary>
public class TaskMaintenanceJobs : BaseTickerQJob
{
    /// <summary>Initializes task maintenance jobs with required dependencies and default state.</summary>
    public TaskMaintenanceJobs(
        IServiceScopeFactory scopeFactory,
        ILogger<TaskMaintenanceJobs> logger,
        SchedulingMetrics metrics)
        : base(scopeFactory, logger, metrics) { }

    /// <summary>Provides the overdue task check operation for task maintenance jobs.</summary>
    [TickerFunction(OverdueTaskCheckHandler.JobName)]
    public async Task OverdueTaskCheckAsync(TickerFunctionContext context, CancellationToken ct)
    {
        await ExecuteJobAsync<OverdueTaskCheckHandler>(OverdueTaskCheckHandler.JobName, context, ct);
    }

    /// <summary>Provides the recurring task generation operation for task maintenance jobs.</summary>
    [TickerFunction(RecurringTaskGenerationHandler.JobName)]
    public async Task RecurringTaskGenerationAsync(TickerFunctionContext context, CancellationToken ct)
    {
        await ExecuteJobAsync<RecurringTaskGenerationHandler>(RecurringTaskGenerationHandler.JobName, context, ct);
    }

    /// <summary>Provides the stale task cleanup operation for task maintenance jobs.</summary>
    [TickerFunction(StaleTaskCleanupHandler.JobName)]
    public async Task StaleTaskCleanupAsync(TickerFunctionContext context, CancellationToken ct)
    {
        await ExecuteJobAsync<StaleTaskCleanupHandler>(StaleTaskCleanupHandler.JobName, context, ct);
    }

    /// <summary>Purges dead-lettered outbox and blob-delete rows past retention.</summary>
    [TickerFunction(OutboxRetentionHandler.JobName)]
    public async Task OutboxRetentionAsync(TickerFunctionContext context, CancellationToken ct)
    {
        await ExecuteJobAsync<OutboxRetentionHandler>(OutboxRetentionHandler.JobName, context, ct);
    }

    /// <summary>Purges consumer inbox claims past retention.</summary>
    [TickerFunction(ConsumerInboxRetentionHandler.JobName)]
    public async Task ConsumerInboxRetentionAsync(TickerFunctionContext context, CancellationToken ct)
    {
        await ExecuteJobAsync<ConsumerInboxRetentionHandler>(ConsumerInboxRetentionHandler.JobName, context, ct);
    }

    /// <summary>Purges executed TickerQ cron occurrences past retention.</summary>
    [TickerFunction(TickerQOccurrenceRetentionHandler.JobName)]
    public async Task TickerQOccurrenceRetentionAsync(TickerFunctionContext context, CancellationToken ct)
    {
        await ExecuteJobAsync<TickerQOccurrenceRetentionHandler>(TickerQOccurrenceRetentionHandler.JobName, context, ct);
    }

    /// <summary>Purges audit log entries past retention.</summary>
    [TickerFunction(AuditRetentionHandler.JobName)]
    public async Task AuditRetentionAsync(TickerFunctionContext context, CancellationToken ct)
    {
        await ExecuteJobAsync<AuditRetentionHandler>(AuditRetentionHandler.JobName, context, ct);
    }
}
