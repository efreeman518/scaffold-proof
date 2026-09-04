namespace TaskFlow.Application.Models.Paging;

/// <summary>
/// Keyset (cursor) list request. There is no page index and no total: paging walks forward from an
/// opaque, tamper-proof cursor so deep pages cost the same as the first (GR-18).
/// </summary>
public record CursorSearchRequest<TFilter, TSortMode>
    where TSortMode : struct, Enum
{
    public TFilter? Filter { get; set; }
    public TSortMode SortMode { get; set; }
    public int PageSize { get; set; } = PageSizeLimits.Default;

    /// <summary>Opaque cursor returned by the previous page; null starts at the first page.</summary>
    public string? Cursor { get; set; }
}
