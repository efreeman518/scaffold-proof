using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts.Storage;

namespace TaskFlow.Infrastructure.Storage.CosmosDb;

/// <summary>Persists and queries no op task view data through infrastructure storage contracts.</summary>
public class NoOpTaskViewRepository(ILogger<NoOpTaskViewRepository> logger) : ITaskViewRepository
{
    /// <summary>Writes upsert to the configured read model store.</summary>
    public Task UpsertAsync(TaskViewDto taskView, CancellationToken ct = default)
    {
        logger.NoOpTaskViewUpsert(taskView.Id);
        return Task.CompletedTask;
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public Task<TaskViewDto?> GetAsync(string id, string tenantId, CancellationToken ct = default)
        => Task.FromResult<TaskViewDto?>(null);

    /// <summary>Queries query by tenant from the configured read model store.</summary>
    public Task<TaskViewPage> QueryByTenantAsync(string tenantId,
        int pageSize = 20, string? continuationToken = null, CancellationToken ct = default)
        => Task.FromResult(new TaskViewPage([], null));

    /// <summary>Applies counter deltas to the configured read model store.</summary>
    public Task PatchCountersAsync(string id, string tenantId,
        IReadOnlyDictionary<string, int> increments, DateTimeOffset lastModifiedUtc, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public Task DeleteAsync(string id, string tenantId, CancellationToken ct = default)
        => Task.CompletedTask;
}
