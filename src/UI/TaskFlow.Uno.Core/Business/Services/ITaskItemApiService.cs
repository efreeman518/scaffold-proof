using TaskFlow.Uno.Core.Business.Models;

namespace TaskFlow.Uno.Core.Business.Services;

/// <summary>Coordinates task item API application use cases with validation, tenant checks, repositories, and response shaping.</summary>
public interface ITaskItemApiService
{
    /// <summary>Searches with keyset (cursor) paging, filters, and a sort mode (GR-18: no offset paging).</summary>
    Task<TaskItemCursorPage> SearchCursorAsync(string? searchTerm = null, string? status = null,
        string? priority = null, Guid? categoryId = null, string sortMode = "IdAsc",
        int pageSize = 50, string? cursor = null, CancellationToken ct = default);
    /// <summary>Tenant task counts by status, overdue, and total in one round trip.</summary>
    Task<TaskItemSummaryModel> GetSummaryAsync(CancellationToken ct = default);
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<TaskItemModel?> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    Task<TaskItemModel> CreateAsync(TaskItemModel model, CancellationToken ct = default);
    /// <summary>Updates existing data after validation and preserves domain invariants. expectedVersion is sent as If-Match ("*" when null).</summary>
    Task<TaskItemModel> UpdateAsync(TaskItemModel model, long? expectedVersion, CancellationToken ct = default);
    /// <summary>Deletes requested data and maps failures to the caller contract. expectedVersion is sent as If-Match ("*" when null).</summary>
    Task DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default);

    // -- TaskItem aggregate children: mutated only through the root's nested routes (GR-15) --
    /// <summary>Adds a Comment to the TaskItem.</summary>
    Task<CommentModel> AddCommentAsync(Guid taskId, string body, CancellationToken ct = default);
    /// <summary>Removes a Comment from the TaskItem. expectedVersion is the root's Version, sent as If-Match.</summary>
    Task RemoveCommentAsync(Guid taskId, Guid commentId, long? expectedVersion, CancellationToken ct = default);
    /// <summary>Adds a ChecklistItem to the TaskItem.</summary>
    Task<ChecklistItemModel> AddChecklistItemAsync(Guid taskId, string title, int sortOrder, CancellationToken ct = default);
    /// <summary>Updates a ChecklistItem on the TaskItem. expectedVersion is the root's Version, sent as If-Match.</summary>
    Task<ChecklistItemModel> UpdateChecklistItemAsync(Guid taskId, ChecklistItemModel item, long? expectedVersion, CancellationToken ct = default);
    /// <summary>Removes a ChecklistItem from the TaskItem. expectedVersion is the root's Version, sent as If-Match.</summary>
    Task RemoveChecklistItemAsync(Guid taskId, Guid checklistItemId, long? expectedVersion, CancellationToken ct = default);
}
