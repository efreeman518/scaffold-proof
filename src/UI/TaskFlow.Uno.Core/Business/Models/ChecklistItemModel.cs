namespace TaskFlow.Uno.Core.Business.Models;

/// <summary>Carries checklist item data between Uno services and presentation models.</summary>
public record ChecklistItemModel
{
    public Guid? Id { get; init; }
    public long? Version { get; init; }
    public string Title { get; init; } = string.Empty;
    public bool IsCompleted { get; init; }
    public int SortOrder { get; init; }
    public DateTimeOffset? CompletedDate { get; init; }
    public Guid TaskItemId { get; init; }
}
