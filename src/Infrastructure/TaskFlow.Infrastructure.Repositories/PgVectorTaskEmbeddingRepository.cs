using EF.Data;
using EF.Domain.Contracts;
using Microsoft.EntityFrameworkCore;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.ReadModel;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// pgvector arm of <see cref="ITaskEmbeddingRepository"/> (D-040). Registered only when the search provider
/// resolves to PgVector, which the registration also proves means the database provider is PostgreSQL, so
/// <c>Set&lt;TaskItemEmbedding&gt;()</c> is always a mapped set here.
/// <para>
/// D-027 split, same as the relational read model: the consumer writes through Trxn (read-your-writes for
/// the row it just wrote) and search reads through Query, the read-replica connection.
/// </para>
/// </summary>
public sealed class PgVectorTaskEmbeddingRepository(TaskFlowDbContextTrxn write, TaskFlowDbContextQuery read)
    : RepositoryBase<TaskFlowDbContextTrxn, string, Guid?>(write), ITaskEmbeddingRepository
{
    /// <inheritdoc />
    public async Task<TaskEmbeddingSource?> GetSourceAsync(
        Guid tenantId, Guid taskItemId, CancellationToken ct = default)
    {
        var id = DomainId.From<TaskItemId>(taskItemId);
        var tenant = DomainId.From<TenantId>(tenantId);

        // Two scalar columns, no aggregate graph: the embedding only ever sees the text.
        return await read.Set<TaskItem>().AsNoTracking()
            .Where(t => t.Id == id && t.TenantId == tenant)
            .Select(t => new TaskEmbeddingSource(t.Title, t.Description))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <inheritdoc />
    public Task UpsertAsync(
        Guid tenantId,
        Guid taskItemId,
        ReadOnlyMemory<float> embedding,
        string modelId,
        DateTimeOffset updatedUtc,
        CancellationToken ct = default)
    {
        // D-028: ON CONFLICT (TenantId, TaskItemId) DO UPDATE. A redelivery re-embeds and replaces rather
        // than failing on the primary key, which is what makes the consumer safe to retry.
        var row = new TaskItemEmbedding
        {
            TenantId = tenantId,
            TaskItemId = taskItemId,
            Embedding = new Vector(embedding),
            Dimensions = embedding.Length,
            ModelId = modelId,
            UpdatedUtc = updatedUtc
        };

        return UpsertAsync(
            row,
            e => new { e.TenantId, e.TaskItemId },
            (existing, proposed) => new TaskItemEmbedding
            {
                Embedding = proposed.Embedding,
                Dimensions = proposed.Dimensions,
                ModelId = proposed.ModelId,
                UpdatedUtc = proposed.UpdatedUtc
            },
            ct);
    }

    /// <inheritdoc />
    public Task DeleteAsync(Guid tenantId, Guid taskItemId, CancellationToken ct = default) =>
        DB.Set<TaskItemEmbedding>()
            .Where(e => e.TenantId == tenantId && e.TaskItemId == taskItemId)
            .ExecuteDeleteAsync(ct);

    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskEmbeddingMatch>> SearchNearestAsync(
        Guid tenantId, ReadOnlyMemory<float> query, int take, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(take, 1);
        var vector = new Vector(query);

        // The ANN query and the task fetch are deliberately two round trips. Joining them in one LINQ
        // query would need TaskItem's strongly-typed key on the join side, which does not translate
        // through its value converter; splitting keeps the vector query at exactly `take` rows and the
        // second query a primary-key lookup of the same count.
        var nearest = await read.Set<TaskItemEmbedding>().AsNoTracking()
            .Where(e => e.TenantId == tenantId)
            .OrderBy(e => e.Embedding.CosineDistance(vector))
            .Take(take)
            .Select(e => new { e.TaskItemId, Distance = e.Embedding.CosineDistance(vector) })
            .ToListAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

        if (nearest.Count == 0) return [];

        var tenant = DomainId.From<TenantId>(tenantId);
        var ids = nearest.Select(n => DomainId.From<TaskItemId>(n.TaskItemId)).ToList();
        var tasks = await read.Set<TaskItem>().AsNoTracking()
            .Where(t => ids.Contains(t.Id) && t.TenantId == tenant)
            .Select(t => new
            {
                Id = t.Id.Value,
                t.Title,
                t.Description,
                t.Status,
                t.Priority,
                CategoryName = t.Category != null ? t.Category.Name : null,
                t.DueDate
            })
            .ToListAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

        var byId = tasks.ToDictionary(t => t.Id);

        // Distance order is the ranking, so the result is driven by the vector query, not by the second
        // fetch. A row whose task was deleted between the two queries is simply dropped.
        return [.. nearest
            .Where(n => byId.ContainsKey(n.TaskItemId))
            .Select(n =>
            {
                var t = byId[n.TaskItemId];
                return new TaskEmbeddingMatch(
                    n.TaskItemId, t.Title, t.Description, t.Status, t.Priority, t.CategoryName, t.DueDate, n.Distance);
            })];
    }
}
