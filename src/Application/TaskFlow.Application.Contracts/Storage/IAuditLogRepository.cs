using EF.Common.Contracts;

namespace TaskFlow.Application.Contracts.Storage;

/// <summary>Persists and queries i audit log data through infrastructure storage contracts.</summary>
public interface IAuditLogRepository
{
    /// <summary>Appends append to the configured audit store.</summary>
    Task AppendAsync<TTenantId>(AuditEntry<string, TTenantId> entry, CancellationToken ct = default);

    /// <summary>
    /// Retention sweep: deletes audit entries recorded before <paramref name="cutoffUtc"/> and returns the row
    /// count. Table Storage transactions are single-partition, which is why the partition key carries the day.
    /// </summary>
    Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default);
}