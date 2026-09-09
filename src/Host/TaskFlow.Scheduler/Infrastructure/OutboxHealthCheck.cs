using Microsoft.Extensions.Diagnostics.HealthChecks;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Observability.Meters;

namespace TaskFlow.Scheduler.Infrastructure;

/// <summary>
/// Reports the outbox backlog: Degraded past 60s of lag or 10k pending rows, Unhealthy past 300s or 50k. Lag,
/// not row count alone, is what tells a dead dispatcher apart from a busy one.
/// </summary>
public sealed class OutboxHealthCheck(IOperationalWorkRepository work, MessagingMetrics metrics) : IHealthCheck
{
    private static readonly TimeSpan DegradedLag = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan UnhealthyLag = TimeSpan.FromSeconds(300);
    private const int DegradedPending = 10_000;
    private const int UnhealthyPending = 50_000;

    /// <inheritdoc />
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var backlog = await work.GetOutboxBacklogAsync(cancellationToken).ConfigureAwait(false);
        metrics.RecordBacklog(backlog.Pending, backlog.Lag, backlog.BlobDeletePending);

        var data = new Dictionary<string, object>
        {
            ["pending"] = backlog.Pending,
            ["deadLettered"] = backlog.DeadLettered,
            ["lagSeconds"] = backlog.Lag.TotalSeconds,
            ["blobDeletePending"] = backlog.BlobDeletePending
        };

        var description = $"outbox pending {backlog.Pending}, dead-lettered {backlog.DeadLettered}, lag {backlog.Lag.TotalSeconds:F0}s";

        if (backlog.Lag > UnhealthyLag || backlog.Pending > UnhealthyPending)
            return HealthCheckResult.Unhealthy(description, data: data);

        if (backlog.Lag > DegradedLag || backlog.Pending > DegradedPending)
            return HealthCheckResult.Degraded(description, data: data);

        return HealthCheckResult.Healthy(description, data);
    }
}
