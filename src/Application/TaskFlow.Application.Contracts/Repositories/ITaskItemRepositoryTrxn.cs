using EF.Data.Contracts;
using EF.Domain.Contracts;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Contracts.Repositories;

/// <summary>Persists and queries i task item data through infrastructure storage contracts.</summary>
public interface ITaskItemRepositoryTrxn : IRepositoryTrxn<TaskItem, TaskItemId>
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<TaskItem?> GetTaskItemAsync(TaskItemId id, bool inclChildren = true, CancellationToken ct = default);
    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    DomainResult<TaskItem> UpdateFromDto(TaskItem entity, TaskItemDto dto, RelatedDeleteBehavior relatedDeleteBehavior = RelatedDeleteBehavior.None);

    // Single-child tracked loads for the lean child-mutation path (see TaskItemChildLoader). Each
    // filters on the owning TaskItemId as well as the child id, so a child of another task can never
    // be mutated through the wrong root.

    /// <summary>Loads one tracked comment owned by the given task item.</summary>
    Task<Comment?> GetCommentAsync(TaskItemId taskItemId, CommentId commentId, CancellationToken ct = default);
    /// <summary>Loads one tracked checklist item owned by the given task item.</summary>
    Task<ChecklistItem?> GetChecklistItemAsync(TaskItemId taskItemId, ChecklistItemId checklistItemId, CancellationToken ct = default);
    /// <summary>Loads one tracked tag association owned by the given task item.</summary>
    Task<TaskItemTag?> GetTaskItemTagAsync(TaskItemId taskItemId, TagId tagId, CancellationToken ct = default);
}
