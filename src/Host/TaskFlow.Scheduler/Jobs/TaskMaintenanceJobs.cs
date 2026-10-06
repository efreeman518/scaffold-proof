using EF.BackgroundServices.TickerQ;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Scheduler.Handlers;
using TaskFlow.Scheduler.Handlers.Retention;
using TickerQ.Utilities.Base;

namespace TaskFlow.Scheduler.Jobs;

/// <summary>
/// The Scheduler's cron jobs. Each schedule is declared on its <see cref="TickerFunctionAttribute"/>: TickerQ's
/// initializer seeds those into the operational store at host start (inside the EF.BackgroundServices.TickerQ seed
/// lock), keyed by function, updating a changed expression and never inserting a second row on restart. Nothing seeds
/// them by hand. All times are UTC (the scheduler time zone). Retention sweeps are staggered off the hour and off each
/// other: they all delete, and running them together would concentrate the lock and log pressure they exist to spread
/// out.
/// <para>
/// TickerQ resolves this class in each execution's scope, so the <see cref="ScheduledJobRunner"/> it receives runs the
/// handler in that scope with one span, the <c>scheduler.job.*</c> metrics, one failure log and TickerQ's cancellation
/// contract. The class must stay top-level: TickerQ's source generator ignores nested job classes.
/// </para>
/// </summary>
public sealed class TaskMaintenanceJobs(ScheduledJobRunner runner)
{
    /// <summary>TickerQ function name of the operational-store retention sweep.</summary>
    public const string TickerQOccurrenceRetention = "TickerQOccurrenceRetention";

    /// <summary>Flags tasks past their due date.</summary>
    [TickerFunction(OverdueTaskCheckHandler.JobName, "0 0 */6 * * *")]
    public Task OverdueTaskCheckAsync(TickerFunctionContext context, CancellationToken ct) =>
        runner.RunAsync<OverdueTaskCheckHandler>(context, ct);

    /// <summary>Generates the next occurrences of recurring tasks.</summary>
    [TickerFunction(RecurringTaskGenerationHandler.JobName, "0 0 2 * * *")]
    public Task RecurringTaskGenerationAsync(TickerFunctionContext context, CancellationToken ct) =>
        runner.RunAsync<RecurringTaskGenerationHandler>(context, ct);

    /// <summary>Deletes long-cancelled tasks.</summary>
    [TickerFunction(StaleTaskCleanupHandler.JobName, "0 0 3 * * 0")]
    public Task StaleTaskCleanupAsync(TickerFunctionContext context, CancellationToken ct) =>
        runner.RunAsync<StaleTaskCleanupHandler>(context, ct);

    /// <summary>
    /// Starts the compliance-check workflow for each tenant with a due compliance task (D-075). At 06:10, off the
    /// 06:00 overdue check.
    /// </summary>
    [TickerFunction(ComplianceCheckHandler.JobName, "0 10 6 * * *")]
    public Task ComplianceCheckAsync(TickerFunctionContext context, CancellationToken ct) =>
        runner.RunAsync<ComplianceCheckHandler>(context, ct);

    /// <summary>Purges dead-lettered outbox and blob-delete rows past retention.</summary>
    [TickerFunction(OutboxRetentionHandler.JobName, "0 15 * * * *")]
    public Task OutboxRetentionAsync(TickerFunctionContext context, CancellationToken ct) =>
        runner.RunAsync<OutboxRetentionHandler>(context, ct);

    /// <summary>Purges consumer inbox claims past retention.</summary>
    [TickerFunction(ConsumerInboxRetentionHandler.JobName, "0 20 * * * *")]
    public Task ConsumerInboxRetentionAsync(TickerFunctionContext context, CancellationToken ct) =>
        runner.RunAsync<ConsumerInboxRetentionHandler>(context, ct);

    /// <summary>
    /// Purges finished TickerQ cron occurrences past <c>Scheduling:Retention:OccurrenceRetention</c> (the package
    /// handler; live rows are never deleted).
    /// </summary>
    [TickerFunction(TickerQOccurrenceRetention, "0 30 4 * * *")]
    public Task TickerQOccurrenceRetentionAsync(TickerFunctionContext context, CancellationToken ct) =>
        runner.RunAsync<TickerQOccurrenceRetentionHandler<TaskFlowTickerQDbContext>>(context, ct);

    /// <summary>Purges audit log entries past retention.</summary>
    [TickerFunction(AuditRetentionHandler.JobName, "0 40 4 * * *")]
    public Task AuditRetentionAsync(TickerFunctionContext context, CancellationToken ct) =>
        runner.RunAsync<AuditRetentionHandler>(context, ct);
}
