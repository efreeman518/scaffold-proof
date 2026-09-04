namespace TaskFlow.Application.Models.Reads;

/// <summary>
/// Full category and tag lists for pickers, capped at <c>PageSizeLimits.MetadataMax</c>. Clients use
/// this instead of a search with an oversized page size, which the [1,100] clamp now rejects.
/// </summary>
public record TaskMetadataDto
{
    public IReadOnlyList<CategoryDto> Categories { get; init; } = [];
    public IReadOnlyList<TagDto> Tags { get; init; } = [];
    public DateTimeOffset GeneratedAtUtc { get; init; }
}
