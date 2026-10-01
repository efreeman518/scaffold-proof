using EF.Audit.Contracts;
using Microsoft.Extensions.Options;
using EF.BackgroundServices.Scheduling;

namespace TaskFlow.Scheduler.Handlers.Retention;

/// <summary>
/// Trims the audit log to its retention window, on whichever arm the Audit:Provider switch selected. On
/// Table Storage deletion goes through single-partition transactions, which is why the audit partition key
/// carries the day: an expired day is one partition per tenant and drops in whole batches instead of one
/// round trip per row. On the relational arm it is a batched delete over the RecordedUtc index.
/// </summary>
public sealed class AuditRetentionHandler(
    IAuditLogRepository auditLog,
    IOptions<AuditSettings> settings,
    ScheduledJobTelemetry telemetry,
    TimeProvider timeProvider) : IScheduledJobHandler
{
    public const string JobName = "AuditRetention";

    /// <summary>Handles audit retention requests.</summary>
    public async Task HandleAsync(CancellationToken ct)
    {
        var cutoffUtc = timeProvider.GetUtcNow().AddDays(-settings.Value.RetentionDays);
        telemetry.RecordRetention("audit", await auditLog.PurgeOlderThanAsync(cutoffUtc, ct));
    }
}
