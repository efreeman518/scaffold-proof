using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Contracts.Aggregates;

/// <summary>
/// Shared child-mutation load path for both application styles. The old path hydrated the whole
/// aggregate (five includes, split query) just to touch one comment; this loads the root without
/// children and, when a specific child is needed, fetches exactly that row tracked. Removals then use
/// the aggregate's entity overload and delete the child through the repository, which works with the
/// child collections unloaded.
/// </summary>
public static class TaskItemChildLoader
{
    /// <summary>
    /// Loads the aggregate root without child collections and enforces the caller's tenant boundary.
    /// Returns (null, null) for a missing root so callers can answer 404 or stay idempotent, and
    /// (null, error) when the tenant boundary rejects the request.
    /// </summary>
    public static async Task<(TaskItem? Entity, string? Error)> LoadRootAsync(
        ITaskItemRepositoryTrxn repoTrxn,
        ITenantBoundaryValidator tenantBoundaryValidator,
        ILogger logger,
        Guid? requestTenantId,
        IReadOnlyCollection<string> requestRoles,
        Guid taskItemId,
        string operation,
        CancellationToken ct)
    {
        var entity = await repoTrxn.GetTaskItemAsync(DomainId.From<TaskItemId>(taskItemId), inclChildren: false, ct);
        if (entity is null) return (null, null);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, requestTenantId, requestRoles, entity.TenantId.Value, operation, nameof(TaskItem), entity.Id.Value);
        return boundary.IsFailure ? (null, boundary.ErrorMessage!) : (entity, null);
    }

    /// <summary>Loads one tracked comment of the aggregate, or null when it does not belong to it.</summary>
    public static Task<Comment?> LoadCommentAsync(
        ITaskItemRepositoryTrxn repoTrxn, Guid taskItemId, Guid commentId, CancellationToken ct) =>
        repoTrxn.GetCommentAsync(DomainId.From<TaskItemId>(taskItemId), DomainId.From<CommentId>(commentId), ct);

    /// <summary>Loads one tracked checklist item of the aggregate, or null when it does not belong to it.</summary>
    public static Task<ChecklistItem?> LoadChecklistItemAsync(
        ITaskItemRepositoryTrxn repoTrxn, Guid taskItemId, Guid checklistItemId, CancellationToken ct) =>
        repoTrxn.GetChecklistItemAsync(DomainId.From<TaskItemId>(taskItemId), DomainId.From<ChecklistItemId>(checklistItemId), ct);

    /// <summary>Loads one tracked tag association of the aggregate, or null when the tag is not associated.</summary>
    public static Task<TaskItemTag?> LoadTaskItemTagAsync(
        ITaskItemRepositoryTrxn repoTrxn, Guid taskItemId, Guid tagId, CancellationToken ct) =>
        repoTrxn.GetTaskItemTagAsync(DomainId.From<TaskItemId>(taskItemId), DomainId.From<TagId>(tagId), ct);
}
