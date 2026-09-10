namespace TaskFlow.Application.Models.Reads;

/// <summary>
/// Full category and tag lists for pickers, capped at <see cref="MetadataMax"/>. Clients use this
/// instead of a search with an oversized page size, which the [1,100] clamp now rejects.
/// </summary>
public record TaskMetadataDto
{
    /// <summary>
    /// Hard cap for the full category/tag lists behind <c>/task-metadata</c>. Deliberately not part of
    /// <c>EF.Common.Contracts.PageSizeLimits</c> (package request 21): that type is the [1,100] range for
    /// paged list reads, and this snapshot is the documented way around it, not another page size.
    /// </summary>
    public const int MetadataMax = 500;

    public IReadOnlyList<CategoryDto> Categories { get; init; } = [];
    public IReadOnlyList<TagDto> Tags { get; init; } = [];
    public DateTimeOffset GeneratedAtUtc { get; init; }
}
