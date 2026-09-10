using EF.Audit.Contracts;
using EF.Common.Contracts;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// Relational arm of <see cref="IAuditLogRepository"/> (D-039), method for method the Azure Table
/// implementation's semantics. <c>AuditRetentionHandler</c> and the audit message handlers are unchanged.
/// </summary>
/// <param name="db">Write context. Audit rows are written on the same connection as the domain work.</param>
/// <param name="systemTenantId">
/// Sentinel tenant for entries with no tenant, carried over from <c>AuditSettings.SystemTenantId</c> so
/// both arms bucket system entries identically.
/// </param>
/// <param name="purgeBatchSize">Rows per retention batch; the default is the shared retention batch size.</param>
public sealed class RelationalAuditLogRepository(
    TaskFlowDbContextTrxn db,
    string systemTenantId,
    int purgeBatchSize = BatchedExecute.DefaultBatchSize) : IAuditLogRepository
{
    /// <inheritdoc />
    public Task AppendAsync<TAuditIdType, TTenantIdType>(
        AuditEntry<TAuditIdType, TTenantIdType> entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var record = new AuditLogRecord
        {
            TenantId = GetTenantId(entry.TenantId) ?? systemTenantId,
            // From the message's own UUIDv7 id, not this consumer's clock: RecordedUtc is part of the
            // primary key, so a replay stamped "now" would insert a second row instead of hitting the
            // upsert below.
            RecordedUtc = UuidV7.TimestampOf(entry.Id),
            Id = entry.Id,
            AuditId = ((object?)entry.AuditId)?.ToString() ?? string.Empty,
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
        return db.AuditLog
            .Upsert(record)
            .On(e => new { e.TenantId, e.RecordedUtc, e.Id })
            .NoUpdate()
            .RunAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task<AuditLogPage> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var rows = db.AuditLog.AsNoTracking();

        if (query.TenantId is not null)
        {
            var tenantId = query.TenantId.Length == 0 ? systemTenantId : query.TenantId;
            rows = rows.Where(e => e.TenantId == tenantId);
        }

        if (query.EntityType is not null) rows = rows.Where(e => e.EntityType == query.EntityType);
        if (query.EntityKey is not null) rows = rows.Where(e => e.EntityKey == query.EntityKey);
        if (query.FromUtc is { } fromUtc) rows = rows.Where(e => e.RecordedUtc >= fromUtc);
        if (query.ToUtc is { } toUtc) rows = rows.Where(e => e.RecordedUtc < toUtc);

        var skip = ParseContinuationToken(query.ContinuationToken);
        // Materialize before projecting: the enum parse and sentinel mapping in ToRecord have no SQL
        // translation.
        var page = await rows
            .OrderByDescending(e => e.RecordedUtc)
            .ThenByDescending(e => e.Id)
            .Skip(skip)
            .Take(query.PageSize)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A short page is the last one; the token is the offset the next page starts at.
        return new AuditLogPage(
            [.. page.Select(e => ToRecord(e, systemTenantId))],
            page.Count < query.PageSize
                ? null
                : (skip + page.Count).ToString(CultureInfo.InvariantCulture));
    }

    /// <inheritdoc />
    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default) =>
        // Batched so one retention run cannot lock the audit table for its whole window; Id is the batch key
        // because it is unique on its own, unlike either half of the composite primary key.
        db.AuditLog
            .Where(e => e.RecordedUtc < cutoffUtc)
            .ExecuteDeleteBatchedAsync(e => e.Id, purgeBatchSize, ct: cancellationToken);

    /// <summary>Projects a stored row onto the backend-neutral record, mapping the sentinel tenant back to null.</summary>
    private static AuditRecord ToRecord(AuditLogRecord record, string systemTenantId) => new()
    {
        Id = record.Id,
        RecordedUtc = record.RecordedUtc,
        AuditId = record.AuditId,
        TenantId = record.TenantId == systemTenantId ? null : record.TenantId,
        EntityType = record.EntityType,
        EntityKey = record.EntityKey,
        Action = record.Action,
        Status = Enum.Parse<AuditStatus>(record.Status),
        StartTime = TimeSpan.FromTicks(record.StartTimeTicks),
        ElapsedTime = TimeSpan.FromTicks(record.ElapsedTimeTicks),
        Metadata = record.Metadata,
        Error = record.Error
    };

    /// <summary>Reads a page offset from an opaque continuation token; a malformed token fails closed.</summary>
    private static int ParseContinuationToken(string? continuationToken)
    {
        if (string.IsNullOrEmpty(continuationToken)) return 0;

        return int.TryParse(continuationToken, CultureInfo.InvariantCulture, out var skip) && skip >= 0
            ? skip
            : throw new ArgumentException($"'{continuationToken}' is not an audit-log continuation token.",
                nameof(continuationToken));
    }

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
