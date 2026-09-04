using EF.Common.Contracts;
using System.Runtime.CompilerServices;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Application.Models.Reads;

namespace TaskFlow.Application.Services;

/// <summary>
/// Style-agnostic aggregate read model. These endpoints have no domain behavior to duplicate - they
/// are projections over the query contexts - so one implementation serves both the Service and CQRS
/// endpoint maps instead of a service class plus a near-identical query handler.
///
/// Deliberately uncached: caching is layered on separately (Phase 3) so the cache decorator can be
/// added and removed without touching the projection logic.
/// </summary>
internal sealed class TaskFlowReadService(
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryQuery taskItemRepoQuery,
    ICategoryRepositoryQuery categoryRepoQuery,
    ITagRepositoryQuery tagRepoQuery) : ITaskFlowReadService
{
    private Guid TenantId => requestContext.TenantId ?? Guid.Empty;

    /// <inheritdoc />
    public Task<TaskItemSummaryDto> GetTaskItemSummaryAsync(CancellationToken ct = default) =>
        taskItemRepoQuery.GetSummaryAsync(TenantId, ct);

    /// <inheritdoc />
    public async Task<TaskMetadataDto> GetTaskMetadataAsync(CancellationToken ct = default)
    {
        var categories = await categoryRepoQuery.GetActiveCategoriesAsync(PageSizeLimits.MetadataMax, ct);
        var tags = await tagRepoQuery.GetTagsAsync(PageSizeLimits.MetadataMax, ct);

        return new TaskMetadataDto
        {
            Categories = categories,
            Tags = tags,
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<TaskItemExportDto> StreamTaskItemExportAsync(
        Guid? afterId, int batchSize, [EnumeratorCancellation] CancellationToken ct = default)
    {
        // One batch per repository call, resuming from the last id yielded: the connection is opened
        // and closed per batch instead of being held for the length of the whole tenant export.
        var cursor = afterId;
        while (true)
        {
            var yielded = 0;
            await foreach (var row in taskItemRepoQuery.StreamExportAsync(TenantId, cursor, batchSize, ct))
            {
                cursor = row.Id;
                yielded++;
                yield return row;
            }

            if (yielded < batchSize) yield break;
        }
    }
}
