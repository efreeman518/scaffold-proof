namespace TaskFlow.Uno.Core.Business.Models;

/// <summary>One keyset (cursor) page of TaskItems. NextCursor is null exactly when HasMore is false.</summary>
public record TaskItemCursorPage
{
    public IReadOnlyList<TaskItemModel> Items { get; init; } = [];
    public string? NextCursor { get; init; }
    public bool HasMore { get; init; }
}
