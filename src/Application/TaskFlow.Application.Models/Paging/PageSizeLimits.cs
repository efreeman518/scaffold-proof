namespace TaskFlow.Application.Models.Paging;

/// <summary>
/// Single source of truth for list page sizes (GR-18). Requests outside [Min, Max] are rejected with
/// 400 rather than silently clamped, so a caller never believes it received more rows than it did.
/// </summary>
public static class PageSizeLimits
{
    public const int Min = 1;
    public const int Max = 100;
    public const int Default = 50;

    /// <summary>Hard cap for the full category/tag lists behind <c>/task-metadata</c>.</summary>
    public const int MetadataMax = 500;

    /// <summary>True when a requested page size is inside the allowed range.</summary>
    public static bool IsValid(int pageSize) => pageSize >= Min && pageSize <= Max;
}
