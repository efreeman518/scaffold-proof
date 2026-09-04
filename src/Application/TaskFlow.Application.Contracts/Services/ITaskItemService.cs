using EF.Common.Contracts;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;

namespace TaskFlow.Application.Contracts.Services;

/// <summary>Coordinates i task item application use cases with validation, tenant checks, repositories, and response shaping.</summary>
///
/// Every mutating method takes the caller's expected aggregate version (GR-16). Null means the caller
/// sent <c>If-Match: *</c> - the explicit trusted-automation override - and the precondition is skipped.
public interface ITaskItemService
{
    /// <summary>Keyset page of task items. Offset paging and totals are gone (GR-18).</summary>
    Task<CursorPage<TaskItemDto>> SearchAsync(TaskItemCursorSearchRequest request, CancellationToken ct = default);
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<Result<DefaultResponse<TaskItemDto>>> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Creates requested data, honoring an optional caller-supplied UUIDv7 id (GR-17).</summary>
    Task<Result<DefaultResponse<TaskItemDto>>> CreateAsync(DefaultRequest<TaskItemDto> request, CancellationToken ct = default);
    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    Task<Result<DefaultResponse<TaskItemDto>>> UpdateAsync(DefaultRequest<TaskItemDto> request, long? expectedVersion, CancellationToken ct = default);
    /// <summary>Applies a sparse partial update (JSON merge patch) - null fields are left unchanged.</summary>
    Task<Result<DefaultResponse<TaskItemDto>>> PatchAsync(Guid id, TaskItemPatchDto patch, long? expectedVersion, CancellationToken ct = default);
    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    Task<Result> DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default);

    // Nested child operations. Comment, ChecklistItem, and the Tag association are internal to the
    // TaskItem aggregate, so they are mutated only through the root (GR-15) - there is no
    // ICommentService.CreateAsync etc. The service loads the aggregate, calls its domain methods,
    // and saves the whole graph in one transaction. Child writes use the ROOT version as their
    // If-Match currency (D-031); child DTO versions are display-only.

    /// <summary>Adds a comment to a TaskItem through the aggregate root.</summary>
    Task<Result<DefaultResponse<CommentDto>>> AddCommentAsync(Guid taskItemId, CommentDto comment, CancellationToken ct = default);
    /// <summary>Updates a comment owned by a TaskItem through the aggregate root.</summary>
    Task<Result<DefaultResponse<CommentDto>>> UpdateCommentAsync(Guid taskItemId, Guid commentId, CommentDto comment, long? expectedVersion, CancellationToken ct = default);
    /// <summary>Removes a comment from a TaskItem through the aggregate root.</summary>
    Task<Result> RemoveCommentAsync(Guid taskItemId, Guid commentId, long? expectedVersion, CancellationToken ct = default);

    /// <summary>Adds a checklist item to a TaskItem through the aggregate root.</summary>
    Task<Result<DefaultResponse<ChecklistItemDto>>> AddChecklistItemAsync(Guid taskItemId, ChecklistItemDto checklistItem, CancellationToken ct = default);
    /// <summary>Updates a checklist item owned by a TaskItem through the aggregate root.</summary>
    Task<Result<DefaultResponse<ChecklistItemDto>>> UpdateChecklistItemAsync(Guid taskItemId, Guid checklistItemId, ChecklistItemDto checklistItem, long? expectedVersion, CancellationToken ct = default);
    /// <summary>Removes a checklist item from a TaskItem through the aggregate root.</summary>
    Task<Result> RemoveChecklistItemAsync(Guid taskItemId, Guid checklistItemId, long? expectedVersion, CancellationToken ct = default);

    /// <summary>Associates an existing Tag with a TaskItem through the aggregate root.</summary>
    Task<Result<DefaultResponse<TaskItemTagDto>>> AssociateTagAsync(Guid taskItemId, Guid tagId, CancellationToken ct = default);
    /// <summary>Removes a Tag association from a TaskItem through the aggregate root.</summary>
    Task<Result> RemoveTagAsync(Guid taskItemId, Guid tagId, long? expectedVersion, CancellationToken ct = default);
}
