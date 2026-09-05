using TaskFlow.Uno.Core.Business.Models;
using TaskFlow.Uno.Core.Business.Notifications;
using TaskFlow.Uno.Core.Client;

namespace TaskFlow.Uno.Core.Business.Services;

/// <summary>Coordinates task item API application use cases with validation, tenant checks, repositories, and response shaping.</summary>
public class TaskItemApiService(
    TaskFlowApiClient client,
    INotificationService notifications) : ITaskItemApiService
{
    /// <summary>Searches with keyset (cursor) paging, filters, and a sort mode.</summary>
    public async Task<TaskItemCursorPage> SearchCursorAsync(string? searchTerm = null,
        string? status = null, string? priority = null, Guid? categoryId = null,
        string sortMode = "IdAsc", int pageSize = 50, string? cursor = null, CancellationToken ct = default)
    {
        var normalizedSearchTerm = NormalizeOptionalFilter(searchTerm);
        var normalizedStatus = NormalizeOptionalFilter(status, "All", "Any");
        var normalizedPriority = NormalizeOptionalFilter(priority, "All", "Any");

        var response = await client.Api.TaskItems.SearchAsync(new TaskItemCursorSearchRequest
        {
            Filter = new TaskItemSearchFilter
            {
                SearchTerm = normalizedSearchTerm,
                Status = normalizedStatus,
                Priority = normalizedPriority,
                CategoryId = categoryId
            },
            SortMode = sortMode,
            PageSize = Math.Max(1, pageSize),
            Cursor = cursor
        }, ct) ?? new CursorPage<TaskItemDto>();

        return new TaskItemCursorPage
        {
            Items = response.Data?.Select(MapToModel).ToList() ?? [],
            NextCursor = response.NextCursor,
            HasMore = response.HasMore
        };
    }

    /// <summary>Tenant task counts by status, overdue, and total in one round trip.</summary>
    public async Task<TaskItemSummaryModel> GetSummaryAsync(CancellationToken ct = default)
    {
        var summary = await client.Api.TaskItems.GetSummaryAsync(ct) ?? new TaskItemSummaryDto();
        return new TaskItemSummaryModel
        {
            ByStatus = (summary.ByStatus ?? []).ToDictionary(s => s.Status, s => s.Count),
            Overdue = summary.Overdue,
            Total = summary.Total
        };
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<TaskItemModel?> GetAsync(Guid id, CancellationToken ct = default)
    {
        var dto = await client.Api.TaskItems[id].GetAsync(cancellationToken: ct);
        return dto is null ? null : MapToModel(dto);
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public async Task<TaskItemModel> CreateAsync(TaskItemModel model, CancellationToken ct = default)
    {
        var dto = MapToDto(model);
        var result = await client.Api.TaskItems.PostAsync(dto, cancellationToken: ct);
        var created = MapToModel(result!);
        await notifications.ShowSuccess($"Created task \"{created.Title}\".", ct: ct);
        return created;
    }

    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    public async Task<TaskItemModel> UpdateAsync(TaskItemModel model, long? expectedVersion, CancellationToken ct = default)
    {
        var dto = MapToDto(model);
        var result = await client.Api.TaskItems[model.Id!.Value].PutAsync(dto, IfMatch(expectedVersion), cancellationToken: ct);
        var updated = MapToModel(result!);
        await notifications.ShowSuccess($"Updated task \"{updated.Title}\".", ct: ct);
        return updated;
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default)
    {
        await client.Api.TaskItems[id].DeleteAsync(IfMatch(expectedVersion), cancellationToken: ct);
        await notifications.ShowSuccess("Task deleted.", ct: ct);
    }

    /// <summary>Adds a comment to the TaskItem aggregate through the root.</summary>
    public async Task<CommentModel> AddCommentAsync(Guid taskId, string body, CancellationToken ct = default)
    {
        var result = await client.Api.TaskItems[taskId].Comments.PostAsync(
            new CommentDto { Body = body, TaskItemId = taskId }, ct);
        return MapToModel(result!);
    }

    /// <summary>Removes a comment from the TaskItem aggregate through the root.</summary>
    public async Task RemoveCommentAsync(Guid taskId, Guid commentId, long? expectedVersion, CancellationToken ct = default) =>
        await client.Api.TaskItems[taskId].Comments.DeleteAsync(commentId, IfMatch(expectedVersion), ct);

    /// <summary>Adds a checklist item to the TaskItem aggregate through the root.</summary>
    public async Task<ChecklistItemModel> AddChecklistItemAsync(Guid taskId, string title, int sortOrder, CancellationToken ct = default)
    {
        var result = await client.Api.TaskItems[taskId].ChecklistItems.PostAsync(
            new ChecklistItemDto { Title = title, SortOrder = sortOrder, TaskItemId = taskId }, ct);
        return MapToModel(result!);
    }

    /// <summary>Updates a checklist item owned by the TaskItem aggregate.</summary>
    public async Task<ChecklistItemModel> UpdateChecklistItemAsync(Guid taskId, ChecklistItemModel item, long? expectedVersion, CancellationToken ct = default)
    {
        var result = await client.Api.TaskItems[taskId].ChecklistItems.PutAsync(
            item.Id!.Value, MapToDto(item), IfMatch(expectedVersion), ct);
        return MapToModel(result!);
    }

    /// <summary>Removes a checklist item from the TaskItem aggregate through the root.</summary>
    public async Task RemoveChecklistItemAsync(Guid taskId, Guid checklistItemId, long? expectedVersion, CancellationToken ct = default) =>
        await client.Api.TaskItems[taskId].ChecklistItems.DeleteAsync(checklistItemId, IfMatch(expectedVersion), ct);

    /// <summary>Formats a Version as the If-Match header value; null means the caller trusts the current state ("*").</summary>
    private static string IfMatch(long? expectedVersion) => expectedVersion?.ToString() ?? "*";

    /// <summary>Normalizes optional filter so callers and persistence use consistent values.</summary>
    private static string? NormalizeOptionalFilter(string? value, params string[] emptyAliases)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Trim();
        return emptyAliases.Any(alias => string.Equals(alias, normalized, StringComparison.OrdinalIgnoreCase))
            ? null
            : normalized;
    }

    /// <summary>Maps to model into the target contract used by callers.</summary>
    private static TaskItemModel MapToModel(TaskItemDto dto) => new()
    {
        Id = dto.Id,
        Version = dto.Version,
        Title = dto.Title ?? string.Empty,
        Description = dto.Description,
        Priority = dto.Priority ?? "None",
        Status = dto.Status ?? "Open",
        Features = dto.Features ?? "None",
        EstimatedEffort = dto.EstimatedEffort,
        ActualEffort = dto.ActualEffort,
        CompletedDate = dto.CompletedDate,
        CategoryId = dto.CategoryId,
        ParentTaskItemId = dto.ParentTaskItemId,
        StartDate = dto.StartDate,
        DueDate = dto.DueDate,
        RecurrenceInterval = dto.RecurrenceInterval,
        RecurrenceFrequency = dto.RecurrenceFrequency,
        RecurrenceEndDate = dto.RecurrenceEndDate,
        CategoryName = dto.CategoryName,
        Comments = dto.Comments?.Select(MapToModel).ToList(),
        ChecklistItems = dto.ChecklistItems?.Select(MapToModel).ToList(),
        Tags = dto.Tags?.Select(t => new TagModel { Id = t.Id, Version = t.Version, Name = t.Name ?? string.Empty, Color = t.Color }).ToList(),
        SubTasks = dto.SubTasks?.Select(MapToModel).ToList()
    };

    private static CommentModel MapToModel(CommentDto c) => new()
    {
        Id = c.Id,
        Version = c.Version,
        Body = c.Body ?? string.Empty,
        TaskItemId = c.TaskItemId ?? Guid.Empty
    };

    private static ChecklistItemModel MapToModel(ChecklistItemDto c) => new()
    {
        Id = c.Id,
        Version = c.Version,
        Title = c.Title ?? string.Empty,
        IsCompleted = c.IsCompleted ?? false,
        SortOrder = c.SortOrder ?? 0,
        CompletedDate = c.CompletedDate,
        TaskItemId = c.TaskItemId ?? Guid.Empty
    };

    /// <summary>Maps to DTO into the target contract used by callers.</summary>
    private static TaskItemDto MapToDto(TaskItemModel model) => new()
    {
        Id = model.Id,
        Version = model.Version,
        Title = model.Title,
        Description = model.Description,
        Priority = model.Priority,
        Status = model.Status,
        Features = model.Features,
        EstimatedEffort = model.EstimatedEffort,
        ActualEffort = model.ActualEffort,
        CategoryId = model.CategoryId,
        ParentTaskItemId = model.ParentTaskItemId,
        StartDate = model.StartDate,
        DueDate = model.DueDate,
        RecurrenceInterval = model.RecurrenceInterval,
        RecurrenceFrequency = model.RecurrenceFrequency,
        RecurrenceEndDate = model.RecurrenceEndDate
        // Children are not sent here: they mutate only through the aggregate root's dedicated
        // nested routes (AddCommentAsync, UpdateChecklistItemAsync, ...), never bundled into
        // the whole-task PUT/POST.
    };

    private static ChecklistItemDto MapToDto(ChecklistItemModel model) => new()
    {
        Id = model.Id,
        Version = model.Version,
        Title = model.Title,
        IsCompleted = model.IsCompleted,
        SortOrder = model.SortOrder,
        CompletedDate = model.CompletedDate,
        TaskItemId = model.TaskItemId
    };
}
