using EF.Common.Contracts;
using EF.Data;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>Persists and queries tag data through infrastructure storage contracts.</summary>
public class TagRepositoryQuery(TaskFlowDbContextQuery db)
    : TaskFlowRepositoryQuery<Tag, TagId>(db), ITagRepositoryQuery
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Tag?> GetTagAsync(TagId id, CancellationToken ct = default)
    {
        return await GetEntityAsync(
            false,
            filter: (Tag t) => t.Id == id,
            cancellationToken: ct
        ).ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <inheritdoc />
    // Metadata list for pickers, hard-capped for the same reason as the category list.
    public async Task<IReadOnlyList<TagDto>> GetTagsAsync(int max, CancellationToken ct = default) =>
        await DB.Set<Tag>()
            .AsNoTracking()
            .OrderBy(t => t.Name).ThenBy(t => t.Id)
            .Take(max)
            .Select(TagMapper.Projection)
            .ToListAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

    /// <summary>Searches search tags and returns filtered results for callers.</summary>
    public async Task<PagedResponse<TagDto>> SearchTagsAsync(SearchRequest<TagSearchFilter> request, bool includeTotal = false, CancellationToken ct = default)
    {
        var q = DB.Set<Tag>().ComposeIQueryable(false);

        // ordering
        if (request.Sorts?.Any() ?? false)
        {
            q = ((IOrderedQueryable<Tag>)q.OrderBy(request.Sorts)).ThenBy(e => e.Id);
        }
        else
        {
            q = q.OrderBy(e => e.Name).ThenBy(e => e.Id);
        }

        // filtering
        var filter = request.Filter;
        if (filter is not null)
        {
            var searchTerm = filter.SearchTerm?.Trim();
            if (!string.IsNullOrWhiteSpace(searchTerm))
                q = q.Where(e => e.Name.Contains(searchTerm));

            if (filter.TenantId.HasValue)
            {
                var tenantId = DomainId.From<TenantId>(filter.TenantId.Value);
                q = q.Where(e => e.TenantId == tenantId);
            }
        }

        (var data, var total) = await q.QueryPageProjectionAsync(TagMapper.Projection,
            pageSize: request.PageSize, pageIndex: Math.Max(1, request.PageIndex),
            includeTotal: includeTotal, splitQueryOptions: SplitQueryThresholdOptions.Default,
            cancellationToken: ct).ConfigureAwait(ConfigureAwaitOptions.None);

        return new PagedResponse<TagDto>
        {
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            Data = data,
            Total = total
        };
    }
}
