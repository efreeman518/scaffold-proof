using TaskFlow.Application.Models.Shared;
using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Application.Models;

/// <summary>Carries task item data across API, application, and UI boundaries.</summary>
public record TaskItemDto : EntityBaseDto, ITenantEntityDto
{
    public Guid TenantId { get; set; }
    public string Title { get; set; } = null!;
    public string? Description { get; set; }
    public Priority Priority { get; set; }
    public TaskItemStatus Status { get; set; }
    public TaskFeatures Features { get; set; }
    public decimal? EstimatedEffort { get; set; }
    public decimal? ActualEffort { get; set; }
    public DateTimeOffset? CompletedDate { get; set; }

    /// <summary>Last write timestamp. Read-only projection; also the keyset sort key of TaskItemSortMode.ModifiedDesc.</summary>
    public DateTimeOffset? ModifiedAtUtc { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? ParentTaskItemId { get; set; }

    // Value objects
    public DateTimeOffset? StartDate { get; set; }
    public DateTimeOffset? DueDate { get; set; }
    public int? RecurrenceInterval { get; set; }
    public string? RecurrenceFrequency { get; set; }
    public DateTimeOffset? RecurrenceEndDate { get; set; }

    // Related
    public List<CommentDto>? Comments { get; set; }
    public List<ChecklistItemDto>? ChecklistItems { get; set; }
    public List<TagDto>? Tags { get; set; }
    public List<AttachmentDto>? Attachments { get; set; }
    public List<TaskItemDto>? SubTasks { get; set; }
    public string? CategoryName { get; set; }
}
