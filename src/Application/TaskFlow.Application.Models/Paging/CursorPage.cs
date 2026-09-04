namespace TaskFlow.Application.Models.Paging;

/// <summary>One keyset page. <c>NextCursor</c> is null exactly when <c>HasMore</c> is false.</summary>
public record CursorPage<T>
{
    public IReadOnlyList<T> Data { get; init; } = [];
    public string? NextCursor { get; init; }
    public bool HasMore { get; init; }
}
