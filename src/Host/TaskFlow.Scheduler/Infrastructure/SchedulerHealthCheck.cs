using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TaskFlow.Infrastructure.Data;
using TickerQ.Utilities.Entities;

namespace TaskFlow.Scheduler.Infrastructure;

/// <summary>
/// Asserts the scheduler is still firing, not merely running. A TickerQ host whose poller has stopped keeps
/// answering "the process is up" while every cron job silently stops - which is exactly the failure a
/// scheduler health check exists to catch. The assertion is that the most recent execution is no older than
/// twice the shortest seeded interval; one missed tick is a blip, two is a stall.
/// </summary>
public sealed class SchedulerHealthCheck(
    IServiceProvider services,
    TimeProvider timeProvider) : IHealthCheck
{
    /// <summary>Shortest seeded cron interval (OverdueTaskCheck, every 6 hours).</summary>
    private static readonly TimeSpan ShortestInterval = TimeSpan.FromHours(6);

    /// <summary>Provides the check health operation for scheduler health check.</summary>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        // The operational store is registered only when Scheduling:UsePersistence is on; without it there
        // is no execution history to assert against, so resolve it optionally rather than assuming it.
        var db = services.GetService<TaskFlowTickerQDbContext>();
        if (db is null)
            return HealthCheckResult.Healthy("Scheduler running without a persisted operational store.");

        var lastExecutedAt = await db.Set<CronTickerOccurrenceEntity<CronTickerEntity>>()
            .AsNoTracking()
            .Where(o => o.ExecutedAt != null)
            .MaxAsync(o => o.ExecutedAt, cancellationToken)
            .ConfigureAwait(false);

        // Nothing has fired yet: a freshly deployed host has not reached its first cron time, and the
        // retention job deletes executed occurrences, so an idle window is expected rather than a fault.
        if (lastExecutedAt is null)
            return HealthCheckResult.Healthy("Scheduler is running; no cron occurrence has executed yet.");

        var age = timeProvider.GetUtcNow() - new DateTimeOffset(lastExecutedAt.Value, TimeSpan.Zero);
        var threshold = ShortestInterval * 2;

        return age > threshold
            ? HealthCheckResult.Degraded(
                $"Last cron execution was {age.TotalHours:F1}h ago, beyond the {threshold.TotalHours:F0}h threshold.")
            : HealthCheckResult.Healthy($"Last cron execution {age.TotalMinutes:F0} minutes ago.");
    }
}
