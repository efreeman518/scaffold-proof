using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Application.Services;

/// <summary>
/// Builds the denormalized TaskView read model consumed by Cosmos-backed task views. The projection consumer
/// calls this after a domain event; it reads authoritative relational data and upserts a projection document,
/// so a retry is safe and a replay is a no-op.
/// </summary>
public class TaskViewProjectionService : ITaskViewProjectionService
{
    private readonly ITaskItemRepositoryQuery _taskItemRepo;
    private readonly IAttachmentRepositoryQuery _attachmentRepo;
    private readonly ITaskViewRepository _taskViewRepo;
    private readonly ILogger<TaskViewProjectionService> _logger;

    /// <summary>Initializes task view projection service with required dependencies and default state.</summary>
    public TaskViewProjectionService(
        ITaskItemRepositoryQuery taskItemRepo,
        IAttachmentRepositoryQuery attachmentRepo,
        ITaskViewRepository taskViewRepo,
        ILogger<TaskViewProjectionService> logger)
    {
        _taskItemRepo = taskItemRepo;
        _attachmentRepo = attachmentRepo;
        _taskViewRepo = taskViewRepo;
        _logger = logger;
    }

    /// <summary>
    /// Rehydrates a task with its related data, computes read-model counters, and writes the TaskView document.
    /// Missing source tasks are logged and skipped because event delivery is asynchronous and can race deletions.
    /// </summary>
    public async Task ProjectTaskItemAsync(Guid taskItemId, DateTimeOffset occurredAtUtc, CancellationToken ct = default)
    {
        var entity = await _taskItemRepo.GetTaskItemAsync(DomainId.From<TaskItemId>(taskItemId), ct);
        if (entity is null)
        {
            _logger.TaskViewNotFoundForProjection(taskItemId);
            return;
        }

        var tenantId = entity.TenantId.Value.ToString();
        var existing = await _taskViewRepo.GetAsync(taskItemId.ToString(), tenantId, ct);

        // Out-of-order redelivery guard: an older event must not overwrite a newer projection. Equal timestamps
        // are allowed through so an exact replay still converges on the same document.
        if (existing is not null && existing.LastModifiedUtc > occurredAtUtc)
        {
            _logger.TaskViewProjectionSkipped(taskItemId, occurredAtUtc, existing.LastModifiedUtc);
            return;
        }

        var taskView = new TaskViewDto
        {
            Id = entity.Id.Value.ToString(),
            TenantId = tenantId,
            Title = entity.Title,
            Description = entity.Description,
            Status = entity.Status.ToString(),
            Priority = entity.Priority.ToString(),
            CategoryName = entity.Category?.Name,
            StartDate = entity.StartDate,
            DueDate = entity.DueDate,
            CompletedDate = entity.CompletedDate,
            IsOverdue = entity.DueDate.HasValue
                        && entity.DueDate < occurredAtUtc
                        && entity.CompletedDate is null,
            Tags = entity.TaskItemTags.Select(tt => tt.Tag?.Name ?? "").Where(n => n.Length > 0).ToList(),
            CommentCount = entity.Comments.Count,
            ChecklistTotal = entity.ChecklistItems.Count,
            ChecklistCompleted = entity.ChecklistItems.Count(ci => ci.IsCompleted),
            AttachmentCount = await _attachmentRepo.CountByOwnerAsync(AttachmentOwnerType.TaskItem, entity.Id.Value, ct),
            SubTaskCount = entity.SubTasks.Count,
            // Both timestamps come from the source and the event, never from the projection run, so a rebuild
            // does not invent a new creation time or hide how stale the read model is.
            LastModifiedUtc = occurredAtUtc,
            CreatedUtc = entity.CreatedAtUtc
        };

        await _taskViewRepo.UpsertAsync(taskView, ct);
        _logger.TaskItemProjected(taskItemId);
    }

    /// <inheritdoc />
    public Task AdjustCountersAsync(
        Guid taskItemId, Guid tenantId, TaskViewCounterDelta delta, DateTimeOffset occurredAtUtc, CancellationToken ct = default)
    {
        // Field names are the JSON property names of TaskViewDocument, which is what a Cosmos patch path addresses.
        var increments = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["commentCount"] = delta.CommentCount,
            ["attachmentCount"] = delta.AttachmentCount,
            ["checklistTotal"] = delta.ChecklistTotal,
            ["checklistCompleted"] = delta.ChecklistCompleted
        };

        return _taskViewRepo.PatchCountersAsync(
            taskItemId.ToString(), tenantId.ToString(), increments, occurredAtUtc, ct);
    }
}
