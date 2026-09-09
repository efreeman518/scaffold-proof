using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Application.Contracts.Repositories;

/// <summary>Text a task contributes to its embedding.</summary>
/// <param name="Title">Task title.</param>
/// <param name="Description">Task description, when it has one.</param>
public sealed record TaskEmbeddingSource(string Title, string? Description);

/// <summary>One semantic-search hit: the task fields the search contract returns plus its cosine distance.</summary>
/// <param name="TaskItemId">Matched task.</param>
/// <param name="Title">Task title.</param>
/// <param name="Description">Task description.</param>
/// <param name="Status">Task status.</param>
/// <param name="Priority">Task priority.</param>
/// <param name="CategoryName">Category name when the task has one.</param>
/// <param name="DueDate">Due date when the task has one.</param>
/// <param name="Distance">Cosine distance from the query vector; 0 is identical, 2 is opposite.</param>
public sealed record TaskEmbeddingMatch(
    Guid TaskItemId,
    string Title,
    string? Description,
    TaskItemStatus Status,
    Priority Priority,
    string? CategoryName,
    DateTimeOffset? DueDate,
    double Distance);

/// <summary>
/// Storage port for the pgvector search projection (D-040). Registered only when
/// <c>Search:Provider</c> resolves to PgVector, so nothing outside that arm can depend on a Postgres-only
/// table. Tenant is an explicit argument on every call, the way it is for the read model and audit sink:
/// the consumer that maintains these rows runs without a request context.
/// </summary>
public interface ITaskEmbeddingRepository
{
    /// <summary>Reads the embeddable text of one task, or null when the task no longer exists.</summary>
    Task<TaskEmbeddingSource?> GetSourceAsync(Guid tenantId, Guid taskItemId, CancellationToken ct = default);

    /// <summary>Creates or replaces the embedding row for one task.</summary>
    Task UpsertAsync(
        Guid tenantId,
        Guid taskItemId,
        ReadOnlyMemory<float> embedding,
        string modelId,
        DateTimeOffset updatedUtc,
        CancellationToken ct = default);

    /// <summary>Removes the embedding row for one task; a missing row is a no-op.</summary>
    Task DeleteAsync(Guid tenantId, Guid taskItemId, CancellationToken ct = default);

    /// <summary>Nearest <paramref name="take"/> tasks for the tenant by cosine distance, closest first.</summary>
    Task<IReadOnlyList<TaskEmbeddingMatch>> SearchNearestAsync(
        Guid tenantId, ReadOnlyMemory<float> query, int take, CancellationToken ct = default);
}
