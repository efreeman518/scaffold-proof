using EF.Common.Contracts;
using EF.Data;
using EF.Data.Contracts;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>Persists and queries comment data through infrastructure storage contracts.</summary>
public class CommentRepositoryQuery(TaskFlowDbContextQuery db)
    : TaskFlowRepositoryQuery<Comment, CommentId>(db), ICommentRepositoryQuery
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Comment?> GetCommentAsync(CommentId id, CancellationToken ct = default)
    {
        return await GetEntityAsync(
            false,
            filter: (Comment c) => c.Id == id,
            cancellationToken: ct
        ).ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <summary>Searches search comments and returns filtered results for callers.</summary>
    public async Task<PagedResponse<CommentDto>> SearchCommentsAsync(SearchRequest<CommentSearchFilter> request, bool includeTotal = false, CancellationToken ct = default)
    {
        var q = DB.Set<Comment>().ComposeIQueryable(false);

        // ordering
        if (request.Sorts?.Any() ?? false)
        {
            q = ((IOrderedQueryable<Comment>)q.OrderBy(request.Sorts)).ThenBy(e => e.Id);
        }
        else
        {
            q = q.OrderBy(e => e.Body).ThenBy(e => e.Id);
        }

        // filtering
        var filter = request.Filter;
        if (filter is not null)
        {
            var searchTerm = filter.SearchTerm?.Trim();
            if (!string.IsNullOrWhiteSpace(searchTerm))
                q = q.Where(e => e.Body.Contains(searchTerm));

            if (filter.TaskItemId.HasValue)
            {
                var taskItemId = DomainId.From<TaskItemId>(filter.TaskItemId.Value);
                q = q.Where(e => e.TaskItemId == taskItemId);
            }

            if (filter.TenantId.HasValue)
            {
                var tenantId = DomainId.From<TenantId>(filter.TenantId.Value);
                q = q.Where(e => e.TenantId == tenantId);
            }
        }

        (var data, var total) = await q.QueryPageProjectionAsync(CommentMapper.Projection,
            pageSize: request.PageSize, pageIndex: Math.Max(1, request.PageIndex),
            includeTotal: includeTotal, splitQueryOptions: SplitQueryThresholdOptions.Default,
            cancellationToken: ct).ConfigureAwait(ConfigureAwaitOptions.None);

        return new PagedResponse<CommentDto>
        {
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            Data = data,
            Total = total
        };
    }
}
