using EF.Common.Contracts;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts.Storage;

namespace TaskFlow.Infrastructure.Storage;

/// <summary>Persists and queries no op audit log data through infrastructure storage contracts.</summary>
public class NoOpAuditLogRepository(ILogger<NoOpAuditLogRepository> logger) : IAuditLogRepository
{
    /// <summary>Appends append to the configured audit store.</summary>
    public Task AppendAsync<TTenantId>(AuditEntry<string, TTenantId> entry, CancellationToken ct = default)
    {
        logger.NoOpAuditPersist(entry.Id);
        return Task.CompletedTask;
    }

    /// <summary>Nothing was stored, so nothing is retained.</summary>
    public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default) =>
        Task.FromResult(0);
}
