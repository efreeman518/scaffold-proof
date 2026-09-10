using EF.Common.Contracts;
using EF.Data;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// Relational audit sink (D-039): the portable-lane arm of <see cref="IAuditLogRepository"/>, method for
/// method the Azure Table implementation's semantics. <c>AuditRetentionHandler</c> and the audit message
/// handlers are unchanged.
/// </summary>
/// <param name="db">Write context. Audit rows are written on the same connection as the domain work.</param>
/// <param name="systemTenantId">
/// Sentinel tenant for entries with no tenant, carried over from
/// <c>AuditLogStorageSettings.NullTenantPartitionKey</c> so both arms bucket system entries identically.
/// </param>
/// <param name="purgeBatchSize">Rows per retention batch; the default is the shared retention batch size.</param>
public sealed class RelationalAuditLogRepository(
    TaskFlowDbContextTrxn db,
    string systemTenantId,
    int purgeBatchSize = BatchedExecute.DefaultBatchSize)
    : RepositoryBase<TaskFlowDbContextTrxn, string, Guid?>(db), IAuditLogRepository
{
    /// <inheritdoc />
    public Task AppendAsync<TTenantId>(AuditEntry<string, TTenantId> entry, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var recordedUtc = DateTimeOffset.UtcNow;
        var record = new AuditLogRecord
        {
            TenantId = GetTenantId(entry.TenantId) ?? systemTenantId,
            RecordedUtc = recordedUtc,
            Id = entry.Id,
            AuditId = entry.AuditId,
            EntityType = entry.EntityType,
            EntityKey = entry.EntityKey,
            Action = entry.Action,
            Status = entry.Status.ToString(),
            StartTimeTicks = entry.StartTime.Ticks,
            ElapsedTimeTicks = entry.ElapsedTime.Ticks,
            Metadata = entry.Metadata,
            Error = entry.Error
        };

        // D-028 upsert rather than Add + SaveChanges, for two reasons: it runs immediately on this
        // connection instead of flushing whatever else the shared write context happens to be tracking, and
        // a replayed entry (same key) is absorbed the way the Table arm's UpsertEntity absorbs it. Insert
        // only - an audit row is written once and never edited, so there is nothing to update.
        return UpsertAsync(record, e => new { e.TenantId, e.RecordedUtc, e.Id }, cancellationToken: ct);
    }

    /// <inheritdoc />
    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default) =>
        // Batched so one retention run cannot lock the audit table for its whole window; Id is the batch key
        // because it is unique on its own, unlike either half of the composite primary key.
        DB.AuditLog.ExecuteDeleteBatchedAsync(
            e => e.RecordedUtc < cutoffUtc, e => e.Id, purgeBatchSize, ct: ct);

    /// <summary>Mirrors the Table arm: a null or empty GUID tenant means "no tenant".</summary>
    private static string? GetTenantId<TTenantId>(TTenantId tenantId)
    {
        object? tenantValue = tenantId;
        return tenantValue switch
        {
            null => null,
            Guid value when value == Guid.Empty => null,
            _ => tenantValue.ToString()
        };
    }
}
