using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Application.Models;

/// <summary>Provides task item search filter behavior for the Application layer.</summary>
public record TaskItemSearchFilter : DefaultSearchFilter
{
    public TaskItemStatus? Status { get; set; }
    public Priority? Priority { get; set; }
    public Guid? CategoryId { get; set; }
    public Guid? ParentTaskItemId { get; set; }
    public DateTimeOffset? DueBefore { get; set; }
    public DateTimeOffset? DueAfter { get; set; }
    public bool? IsOverdue { get; set; }

    /// <summary>Only tasks associated with the caller tenant's tag of this name, compared case-insensitively.</summary>
    public string? TagName { get; set; }
}
