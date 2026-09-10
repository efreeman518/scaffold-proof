using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Repositories;
using TaskFlow.Observability.Meters;
using TaskFlow.Scheduler.Abstractions;
using TickerQ.Utilities.Entities;

namespace TaskFlow.Scheduler.Handlers.Retention;

/// <summary>
/// Trims TickerQ's cron occurrence history. TickerQ writes one row per fire of every cron ticker and never
/// removes them, so on a six-hourly job this table grows without bound; the rows are execution history, not
/// scheduling state, and the dashboard only shows recent ones.
/// </summary>
public sealed class TickerQOccurrenceRetentionHandler(
    TaskFlowTickerQDbContext db,
    SchedulerJobMeter meter,
    TimeProvider timeProvider,
    IConfiguration config) : IScheduledJobHandler
{
    public const string JobName = "TickerQOccurrenceRetention";
    private const int DefaultRetentionHours = 24;

    /// <summary>Handles TickerQ occurrence retention requests.</summary>
    public async Task HandleAsync(CancellationToken ct)
    {
        var cutoff = timeProvider.GetUtcNow()
            .AddHours(-config.GetValue("Scheduling:Retention:TickerQOccurrenceHours", DefaultRetentionHours))
            .UtcDateTime;

        // Only executed occurrences: a row still queued or in flight is live scheduling state.
        var deleted = await db.Set<CronTickerOccurrenceEntity<CronTickerEntity>>()
            .ExecuteDeleteBatchedAsync(o => o.ExecutedAt != null && o.ExecutedAt < cutoff, o => o.Id, ct: ct);

        meter.RecordRetention("tickerq", deleted);
    }
}
