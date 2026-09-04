using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Application.Models.Reads;

/// <summary>
/// Flat NDJSON export row. Scalars only - no child collections - so the export stream stays a single
/// forward-only projection with no cartesian explosion.
/// </summary>
public record TaskItemExportDto
{
    public Guid Id { get; init; }
    public Guid TenantId { get; init; }
    public string Title { get; init; } = null!;
    public string? Description { get; init; }
    public Priority Priority { get; init; }
    public TaskItemStatus Status { get; init; }
    public decimal? EstimatedEffort { get; init; }
    public decimal? ActualEffort { get; init; }
    public DateTimeOffset? StartDate { get; init; }
    public DateTimeOffset? DueDate { get; init; }
    public DateTimeOffset? CompletedDate { get; init; }
    public Guid? CategoryId { get; init; }
    public Guid? ParentTaskItemId { get; init; }
    public long Version { get; init; }
    public DateTimeOffset CreatedAtUtc { get; init; }
    public DateTimeOffset ModifiedAtUtc { get; init; }
}
