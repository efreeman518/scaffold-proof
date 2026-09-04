using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Application.Services;

/// <summary>
/// Builds the denormalized TaskView read model consumed by Cosmos-backed task views.
/// Function triggers call this after domain events; it reads authoritative SQL data and upserts
/// a projection document so projection retries are safe.
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
    /// Rehydrates a task with its related data, computes read-model counters, and writes the
    /// TaskView document. Missing source tasks are logged and skipped because event delivery is
    /// asynchronous and can race deletions.
    /// </summary>
    public async Task ProjectTaskItemAsync(Guid taskItemId, CancellationToken ct = default)
    {
        var entity = await _taskItemRepo.GetTaskItemAsync(DomainId.From<TaskItemId>(taskItemId), ct);
        if (entity is null)
        {
            _logger.LogWarning("TaskItem {Id} not found for projection", taskItemId);
            return;
        }

        var taskView = new TaskViewDto
        {
            Id = entity.Id.Value.ToString(),
            TenantId = entity.TenantId.Value.ToString(),
            Title = entity.Title,
            Description = entity.Description,
            Status = entity.Status.ToString(),
            Priority = entity.Priority.ToString(),
            CategoryName = entity.Category?.Name,
            StartDate = entity.StartDate,
            DueDate = entity.DueDate,
            CompletedDate = entity.CompletedDate,
            IsOverdue = entity.DueDate.HasValue
                        && entity.DueDate < DateTimeOffset.UtcNow
                        && entity.CompletedDate is null,
            Tags = entity.TaskItemTags.Select(tt => tt.Tag?.Name ?? "").Where(n => n.Length > 0).ToList(),
            CommentCount = entity.Comments.Count,
            ChecklistTotal = entity.ChecklistItems.Count,
            ChecklistCompleted = entity.ChecklistItems.Count(ci => ci.IsCompleted),
            AttachmentCount = await _attachmentRepo.CountByOwnerAsync(AttachmentOwnerType.TaskItem, entity.Id.Value, ct),
            SubTaskCount = entity.SubTasks.Count,
            LastModifiedUtc = DateTimeOffset.UtcNow,
            CreatedUtc = DateTimeOffset.UtcNow
        };

        await _taskViewRepo.UpsertAsync(taskView, ct);
        _logger.TaskItemProjected(taskItemId);
    }
}
