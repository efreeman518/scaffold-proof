using TaskFlow.Application.Models.Reads;

namespace TaskFlow.Application.Contracts.Services;

/// <summary>
/// Style-agnostic read model for aggregate reporting endpoints. These are pure projections with no
/// domain behavior, so they are registered once in AddSharedApplicationServices and used by both the
/// Service and CQRS endpoint maps - the documented exception to the style split.
/// </summary>
public interface ITaskFlowReadService
{
    /// <summary>Tenant-wide task counts by status plus overdue and total, in one round trip.</summary>
    Task<TaskItemSummaryDto> GetTaskItemSummaryAsync(CancellationToken ct = default);

    /// <summary>Full category and tag lists for pickers, capped at the metadata limit.</summary>
    Task<TaskMetadataDto> GetTaskMetadataAsync(CancellationToken ct = default);

    /// <summary>
    /// Streams the tenant's tasks as flat rows, resuming after <paramref name="afterId"/>. The caller
    /// loops one batch at a time so no database connection is held open across the whole tenant.
    /// </summary>
    IAsyncEnumerable<TaskItemExportDto> StreamTaskItemExportAsync(Guid? afterId, int batchSize, CancellationToken ct = default);
}
