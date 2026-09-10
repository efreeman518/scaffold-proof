using EF.Audit.Contracts;
using EF.Common.Contracts;
using Microsoft.Extensions.Logging;

namespace TaskFlow.Infrastructure.Storage;

/// <summary>Audit sink for deployments with no audit backend configured: logs the entry and drops it.</summary>
public class NoOpAuditLogRepository(ILogger<NoOpAuditLogRepository> logger) : IAuditLogRepository
{
    /// <summary>Appends append to the configured audit store.</summary>
    public Task AppendAsync<TAuditIdType, TTenantIdType>(
        AuditEntry<TAuditIdType, TTenantIdType> entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        logger.NoOpAuditPersist(entry.Id);
        return Task.CompletedTask;
    }

    /// <summary>Nothing was stored, so every page is empty.</summary>
    public Task<AuditLogPage> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken = default) =>
        Task.FromResult(new AuditLogPage([], null));

    /// <summary>Nothing was stored, so nothing is retained.</summary>
    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default) =>
        Task.FromResult(0);
}
