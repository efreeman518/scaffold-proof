using EF.Common.Contracts;
using Microsoft.Extensions.AI;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Models.Paging;

namespace TaskFlow.Infrastructure.AI.Search;

/// <summary>
/// pgvector arm of the search switch (D-040). Only <see cref="SearchMode.Semantic"/> is answered from the
/// vector index; every other mode is the SQL prefix query, delegated to the injected
/// <see cref="NoOpSearchService"/> rather than reimplemented, so both arms cannot drift.
/// <para>
/// Indexing is not an operation here: <c>TaskEmbeddingConsumer</c> owns the projection, driven by the same
/// events as every other read model, so an inline index call would be a second, racing writer.
/// </para>
/// </summary>
/// <param name="embeddings">Vector storage port.</param>
/// <param name="generator">Embedding generator; must be the model that produced the stored vectors.</param>
/// <param name="prefixSearch">The SQL prefix arm, used for every non-semantic mode.</param>
public sealed class PgVectorSearchService(
    ITaskEmbeddingRepository embeddings,
    IEmbeddingGenerator<string, Embedding<float>> generator,
    NoOpSearchService prefixSearch) : ITaskFlowSearchService
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<TaskItemSearchResult>> SearchTaskItemsAsync(
        string query, SearchMode mode, Guid? tenantId, int maxResults = 10, CancellationToken ct = default)
    {
        if (mode != SearchMode.Semantic)
            return await prefixSearch.SearchTaskItemsAsync(query, mode, tenantId, maxResults, ct).ConfigureAwait(false);

        // GR-19: semantic results are tenant-scoped like every other list read. Without a tenant there is no
        // scope to search, and an unscoped vector query would cross tenants.
        if (tenantId is null || string.IsNullOrWhiteSpace(query)) return [];

        var take = Math.Clamp(maxResults, PageSizeLimits.Min, PageSizeLimits.Max);
        var embedding = await generator.GenerateAsync(query, cancellationToken: ct).ConfigureAwait(false);
        var matches = await embeddings
            .SearchNearestAsync(tenantId.Value, embedding.Vector, take, ct)
            .ConfigureAwait(false);

        return [.. matches.Select(m => new TaskItemSearchResult
        {
            Id = m.TaskItemId.ToString(),
            Title = m.Title,
            Description = m.Description,
            Status = m.Status.ToString(),
            Priority = m.Priority.ToString(),
            CategoryName = m.CategoryName,
            DueDate = m.DueDate,
            // Cosine distance runs 0 (identical) to 2 (opposite); the contract's Score is a similarity, so
            // it is reported the way the Azure arm reports one - higher is closer.
            Score = 1d - m.Distance
        })];
    }

    /// <inheritdoc />
    public Task IndexTaskItemAsync(TaskItemSearchDocument document, CancellationToken ct = default) =>
        prefixSearch.IndexTaskItemAsync(document, ct);

    /// <inheritdoc />
    public Task RemoveTaskItemAsync(string taskItemId, CancellationToken ct = default) =>
        prefixSearch.RemoveTaskItemAsync(taskItemId, ct);
}
