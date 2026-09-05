using Microsoft.Extensions.Options;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Storage;
using TaskFlow.Observability.Meters;
using TaskFlow.Scheduler.Abstractions;

namespace TaskFlow.Scheduler.Handlers.Retention;

/// <summary>
/// Trims the Table Storage audit log to its retention window. Deletion goes through single-partition
/// transactions of 100, which is why the audit partition key carries the day: an expired day is one partition
/// per tenant and drops in whole batches instead of one round trip per row.
/// </summary>
public sealed class AuditRetentionHandler(
    IAuditLogRepository auditLog,
    IOptions<AuditLogStorageSettings> settings,
    SchedulerJobMeter meter,
    TimeProvider timeProvider) : IScheduledJobHandler
{
    public const string JobName = "AuditRetention";

    /// <summary>Handles audit retention requests.</summary>
    public async Task HandleAsync(CancellationToken ct)
    {
        var cutoffUtc = timeProvider.GetUtcNow().AddDays(-settings.Value.RetentionDays);
        meter.RecordRetention("audit", await auditLog.PurgeOlderThanAsync(cutoffUtc, ct));
    }
}
