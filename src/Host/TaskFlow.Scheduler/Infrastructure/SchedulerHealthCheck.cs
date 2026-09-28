using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TaskFlow.Infrastructure.Data;
using TickerQ.Utilities.Entities;

namespace TaskFlow.Scheduler.Infrastructure;

/// <summary>
/// Asserts the scheduler is still firing, not merely running. A TickerQ host whose poller has stopped keeps
/// answering "the process is up" while every cron job silently stops - which is exactly the failure a
/// scheduler health check exists to catch. The assertion is that the most recent execution is no older than
/// twice the shortest seeded interval; one missed tick is a blip, two is a stall. A host that has been up for
/// longer than that without any execution at all is the same stall - one whose jobs never started.
/// </summary>
public sealed class SchedulerHealthCheck(
    IServiceProvider services,
    SchedulerStartTime startTime,
    TimeProvider timeProvider) : IHealthCheck
{
    /// <summary>Shortest seeded cron interval (OverdueTaskCheck, every 6 hours).</summary>
    private static readonly TimeSpan ShortestInterval = TimeSpan.FromHours(6);

    /// <summary>Oldest acceptable last execution, and the grace a fresh host gets before its first one.</summary>
    public static readonly TimeSpan StallThreshold = ShortestInterval * 2;

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

        var now = timeProvider.GetUtcNow();

        // Nothing has fired yet. Inside the grace window that is expected - a freshly started host has not
        // reached its first cron time. Past it, the jobs are not running at all: the retention job keeps at
        // least one recent occurrence of every job that runs more often than the retention window.
        if (lastExecutedAt is null)
        {
            var uptime = startTime.StartedAtUtc is { } started ? now - started : TimeSpan.Zero;
            return uptime > StallThreshold
                ? HealthCheckResult.Degraded(
                    $"No cron occurrence has executed in {uptime.TotalHours:F1}h of uptime, beyond the {StallThreshold.TotalHours:F0}h threshold.")
                : HealthCheckResult.Healthy("Scheduler is running; no cron occurrence has executed yet.");
        }

        var age = now - new DateTimeOffset(lastExecutedAt.Value, TimeSpan.Zero);

        return age > StallThreshold
            ? HealthCheckResult.Degraded(
                $"Last cron execution was {age.TotalHours:F1}h ago, beyond the {StallThreshold.TotalHours:F0}h threshold.")
            : HealthCheckResult.Healthy($"Last cron execution {age.TotalMinutes:F0} minutes ago.");
    }
}
