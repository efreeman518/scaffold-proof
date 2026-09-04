using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Application.Models.Reads;

/// <summary>One status bucket of the tenant task summary.</summary>
public record TaskItemStatusCountDto(TaskItemStatus Status, int Count);

/// <summary>
/// Tenant-wide task counts computed in one database round trip. Replaces the dashboard's former
/// pattern of paging every task client-side.
/// </summary>
public record TaskItemSummaryDto
{
    public IReadOnlyList<TaskItemStatusCountDto> ByStatus { get; init; } = [];
    public int Overdue { get; init; }
    public int Total { get; init; }
    public DateTimeOffset GeneratedAtUtc { get; init; }
}
