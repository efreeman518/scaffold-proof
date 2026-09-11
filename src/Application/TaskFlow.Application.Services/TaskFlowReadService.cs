using EF.Cache;
using EF.Common.Contracts;
using System.Runtime.CompilerServices;
using TaskFlow.Application.Contracts.Caching;
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
/// The two snapshot reads are cached; the export is not. An export is a full-tenant stream read once and
/// resumed by cursor - caching it would hold a whole tenant in memory to serve a request that is already
/// streaming, and the pages a caller resumes into would be a mix of snapshot ages.
/// </summary>
internal sealed class TaskFlowReadService(
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryQuery taskItemRepoQuery,
    ICategoryRepositoryQuery categoryRepoQuery,
    ITagRepositoryQuery tagRepoQuery,
    ITypedCache cache) : ITaskFlowReadService
{
    private Guid TenantId => requestContext.TenantId ?? Guid.Empty;

    /// <inheritdoc />
    // Summary profile: seconds, because a dashboard count is visibly wrong when stale. The soft timeout
    // returns the previous snapshot rather than making every caller wait on a slow aggregate.
    public Task<TaskItemSummaryDto> GetTaskItemSummaryAsync(CancellationToken ct = default) =>
        cache.GetOrSetAsync(
            CacheKind.TaskSummary,
            TenantId,
            token => taskItemRepoQuery.GetSummaryAsync(TenantId, token),
            CacheProfiles.Summary,
            ct: ct);

    /// <inheritdoc />
    // Metadata profile: minutes, eagerly refreshed. Category and tag lists change rarely and every picker
    // in every client asks for them.
    public Task<TaskMetadataDto> GetTaskMetadataAsync(CancellationToken ct = default) =>
        cache.GetOrSetAsync(
            CacheKind.TaskMetadata,
            TenantId,
            BuildMetadataAsync,
            CacheProfiles.Metadata,
            ct: ct);

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

    /// <summary>Builds the metadata snapshot. Immutable by construction - nothing cached is an entity.</summary>
    private async Task<TaskMetadataDto> BuildMetadataAsync(CancellationToken ct)
    {
        var categories = await categoryRepoQuery.GetActiveCategoriesAsync(TaskMetadataDto.MetadataMax, ct);
        var tags = await tagRepoQuery.GetTagsAsync(TaskMetadataDto.MetadataMax, ct);

        return new TaskMetadataDto
        {
            Categories = categories,
            Tags = tags,
            GeneratedAtUtc = DateTimeOffset.UtcNow
        };
    }
}
