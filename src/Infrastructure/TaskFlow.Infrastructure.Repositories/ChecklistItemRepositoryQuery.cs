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

/// <summary>Persists and queries checklist item data through infrastructure storage contracts.</summary>
public class ChecklistItemRepositoryQuery(TaskFlowDbContextQuery db)
    : TaskFlowRepositoryQuery<ChecklistItem, ChecklistItemId>(db), IChecklistItemRepositoryQuery
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<ChecklistItem?> GetChecklistItemAsync(ChecklistItemId id, CancellationToken ct = default)
    {
        return await GetEntityAsync(
            false,
            filter: (ChecklistItem ci) => ci.Id == id,
            cancellationToken: ct
        ).ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <summary>Searches search checklist items and returns filtered results for callers.</summary>
    public async Task<PagedResponse<ChecklistItemDto>> SearchChecklistItemsAsync(SearchRequest<ChecklistItemSearchFilter> request, CancellationToken ct = default)
    {
        var q = DB.Set<ChecklistItem>().ComposeIQueryable(false);

        // ordering
        if (request.Sorts?.Any() ?? false)
        {
            q = ((IOrderedQueryable<ChecklistItem>)q.OrderBy(request.Sorts)).ThenBy(e => e.Id);
        }
        else
        {
            q = q.OrderBy(e => e.SortOrder).ThenBy(e => e.Id);
        }

        // filtering
        var filter = request.Filter;
        if (filter is not null)
        {
            var searchTerm = filter.SearchTerm?.Trim();
            if (!string.IsNullOrWhiteSpace(searchTerm))
                q = q.Where(e => e.Title.Contains(searchTerm));

            if (filter.TaskItemId.HasValue)
            {
                var taskItemId = DomainId.From<TaskItemId>(filter.TaskItemId.Value);
                q = q.Where(e => e.TaskItemId == taskItemId);
            }

            if (filter.IsCompleted.HasValue)
            {
                var isCompleted = filter.IsCompleted.Value;
                q = q.Where(e => e.IsCompleted == isCompleted);
            }

            if (filter.TenantId.HasValue)
            {
                var tenantId = DomainId.From<TenantId>(filter.TenantId.Value);
                q = q.Where(e => e.TenantId == tenantId);
            }
        }

        (var data, var total) = await q.QueryPageProjectionAsync(ChecklistItemMapper.Projection,
            pageSize: request.PageSize, pageIndex: Math.Max(1, request.PageIndex),
            includeTotal: true, splitQueryOptions: SplitQueryThresholdOptions.Default,
            cancellationToken: ct).ConfigureAwait(ConfigureAwaitOptions.None);

        return new PagedResponse<ChecklistItemDto>
        {
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            Data = data,
            Total = total
        };
    }
}
