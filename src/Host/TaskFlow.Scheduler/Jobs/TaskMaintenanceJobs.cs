using TaskFlow.Scheduler.Handlers;
using TaskFlow.Scheduler.Handlers.Retention;
using TaskFlow.Scheduler.Telemetry;
using TickerQ.Utilities.Base;

namespace TaskFlow.Scheduler.Jobs;

/// <summary>
/// The Scheduler's cron jobs. Each schedule is declared on its <see cref="TickerFunctionAttribute"/>: TickerQ's
/// initializer seeds those into the operational store at host start, keyed by function, updating a changed
/// expression and never inserting a second row on restart. Nothing seeds them by hand - a manual
/// <c>ICronTickerManager.AddAsync</c> before start finds no registered function and persists nothing, and one after
/// start inserts a duplicate ticker per restart and replica. All times are UTC (the scheduler time zone).
/// Retention sweeps are staggered off the hour and off each other: they all delete, and running them together
/// would concentrate the lock and log pressure they exist to spread out.
/// </summary>
public class TaskMaintenanceJobs : BaseTickerQJob
{
    /// <summary>Initializes task maintenance jobs with required dependencies and default state.</summary>
    public TaskMaintenanceJobs(
        IServiceScopeFactory scopeFactory,
        ILogger<TaskMaintenanceJobs> logger,
        SchedulingMetrics metrics)
        : base(scopeFactory, logger, metrics) { }

    /// <summary>Provides the overdue task check operation for task maintenance jobs.</summary>
    [TickerFunction(OverdueTaskCheckHandler.JobName, "0 0 */6 * * *")]
    public async Task OverdueTaskCheckAsync(TickerFunctionContext context, CancellationToken ct)
    {
        await ExecuteJobAsync<OverdueTaskCheckHandler>(OverdueTaskCheckHandler.JobName, context, ct);
    }

    /// <summary>Provides the recurring task generation operation for task maintenance jobs.</summary>
    [TickerFunction(RecurringTaskGenerationHandler.JobName, "0 0 2 * * *")]
    public async Task RecurringTaskGenerationAsync(TickerFunctionContext context, CancellationToken ct)
    {
        await ExecuteJobAsync<RecurringTaskGenerationHandler>(RecurringTaskGenerationHandler.JobName, context, ct);
    }

    /// <summary>Provides the stale task cleanup operation for task maintenance jobs.</summary>
    [TickerFunction(StaleTaskCleanupHandler.JobName, "0 0 3 * * 0")]
    public async Task StaleTaskCleanupAsync(TickerFunctionContext context, CancellationToken ct)
    {
        await ExecuteJobAsync<StaleTaskCleanupHandler>(StaleTaskCleanupHandler.JobName, context, ct);
    }

    /// <summary>Purges dead-lettered outbox and blob-delete rows past retention.</summary>
    [TickerFunction(OutboxRetentionHandler.JobName, "0 15 * * * *")]
    public async Task OutboxRetentionAsync(TickerFunctionContext context, CancellationToken ct)
    {
        await ExecuteJobAsync<OutboxRetentionHandler>(OutboxRetentionHandler.JobName, context, ct);
    }

    /// <summary>Purges consumer inbox claims past retention.</summary>
    [TickerFunction(ConsumerInboxRetentionHandler.JobName, "0 20 * * * *")]
    public async Task ConsumerInboxRetentionAsync(TickerFunctionContext context, CancellationToken ct)
    {
        await ExecuteJobAsync<ConsumerInboxRetentionHandler>(ConsumerInboxRetentionHandler.JobName, context, ct);
    }

    /// <summary>Purges executed TickerQ cron occurrences past retention.</summary>
    [TickerFunction(TickerQOccurrenceRetentionHandler.JobName, "0 30 4 * * *")]
    public async Task TickerQOccurrenceRetentionAsync(TickerFunctionContext context, CancellationToken ct)
    {
        await ExecuteJobAsync<TickerQOccurrenceRetentionHandler>(TickerQOccurrenceRetentionHandler.JobName, context, ct);
    }

    /// <summary>Purges audit log entries past retention.</summary>
    [TickerFunction(AuditRetentionHandler.JobName, "0 40 4 * * *")]
    public async Task AuditRetentionAsync(TickerFunctionContext context, CancellationToken ct)
    {
        await ExecuteJobAsync<AuditRetentionHandler>(AuditRetentionHandler.JobName, context, ct);
    }
}
