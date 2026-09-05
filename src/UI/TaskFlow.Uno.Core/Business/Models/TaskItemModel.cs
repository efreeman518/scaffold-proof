namespace TaskFlow.Uno.Core.Business.Models;

/// <summary>Carries task item data between Uno services and presentation models.</summary>
public record TaskItemModel
{
    public Guid? Id { get; init; }
    public long? Version { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Priority { get; init; } = "None";
    public string Status { get; init; } = "Open";
    public string Features { get; init; } = "None";
    public decimal? EstimatedEffort { get; init; }
    public decimal? ActualEffort { get; init; }
    public DateTimeOffset? CompletedDate { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? ParentTaskItemId { get; init; }
    public DateTimeOffset? StartDate { get; init; }
    public DateTimeOffset? DueDate { get; init; }
    public int? RecurrenceInterval { get; init; }
    public string? RecurrenceFrequency { get; init; }
    public DateTimeOffset? RecurrenceEndDate { get; init; }
    public string? CategoryName { get; init; }
    public IReadOnlyList<CommentModel>? Comments { get; init; }
    public IReadOnlyList<ChecklistItemModel>? ChecklistItems { get; init; }
    public IReadOnlyList<TagModel>? Tags { get; init; }
    public IReadOnlyList<AttachmentModel>? Attachments { get; init; }
    public IReadOnlyList<TaskItemModel>? SubTasks { get; init; }

    public bool IsOverdue => DueDate.HasValue && DueDate.Value < DateTimeOffset.UtcNow
                             && Status is not ("Completed" or "Cancelled");
}
