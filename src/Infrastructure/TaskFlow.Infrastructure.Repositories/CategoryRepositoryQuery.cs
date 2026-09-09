using EF.Common.Contracts;
using EF.Data;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using System.Linq.Expressions;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>Persists and queries category data through infrastructure storage contracts.</summary>
public class CategoryRepositoryQuery(TaskFlowDbContextQuery db)
    : TaskFlowRepositoryQuery<Category, CategoryId>(db), ICategoryRepositoryQuery
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Category?> GetCategoryAsync(CategoryId id, CancellationToken ct = default)
    {
        var includesList = new List<Expression<Func<IQueryable<Category>, IIncludableQueryable<Category, object?>>>>
        {
            q => q.Include(c => c.SubCategories)
        };

        return await GetEntityAsync(
            false,
            filter: c => c.Id == id,
            splitQueryThresholdOptions: SplitQueryThresholdOptions.Default,
            includes: [.. includesList],
            cancellationToken: ct
        ).ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <inheritdoc />
    // Metadata list for pickers: active categories only, hard-capped so a tenant with a runaway
    // category tree cannot turn /task-metadata into an unbounded read.
    public async Task<IReadOnlyList<CategoryDto>> GetActiveCategoriesAsync(int max, CancellationToken ct = default) =>
        await DB.Set<Category>()
            .AsNoTracking()
            .Where(c => c.IsActive)
            .OrderBy(c => c.SortOrder).ThenBy(c => c.Name).ThenBy(c => c.Id)
            .Take(max)
            .Select(CategoryMapper.Projection)
            .ToListAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

    /// <summary>Searches search categories and returns filtered results for callers.</summary>
    public async Task<PagedResponse<CategoryDto>> SearchCategoriesAsync(SearchRequest<CategorySearchFilter> request, bool includeTotal = false, CancellationToken ct = default)
    {
        var q = DB.Set<Category>().ComposeIQueryable(false);

        // ordering
        if (request.Sorts?.Any() ?? false)
        {
            q = ((IOrderedQueryable<Category>)q.OrderBy(request.Sorts)).ThenBy(e => e.Id);
        }
        else
        {
            q = q.OrderBy(e => e.SortOrder).ThenBy(e => e.Name).ThenBy(e => e.Id);
        }

        // filtering
        var filter = request.Filter;
        if (filter is not null)
        {
            var searchTerm = filter.SearchTerm?.Trim();
            if (!string.IsNullOrWhiteSpace(searchTerm))
                q = q.Where(e => e.Name.Contains(searchTerm));

            if (filter.IsActive.HasValue)
            {
                var isActive = filter.IsActive.Value;
                q = q.Where(e => e.IsActive == isActive);
            }

            if (filter.ParentCategoryId.HasValue)
            {
                var parentCategoryId = DomainId.From<CategoryId>(filter.ParentCategoryId.Value);
                q = q.Where(e => e.ParentCategoryId == parentCategoryId);
            }

            if (filter.TenantId.HasValue)
            {
                var tenantId = DomainId.From<TenantId>(filter.TenantId.Value);
                q = q.Where(e => e.TenantId == tenantId);
            }
        }

        (var data, var total) = await q.QueryPageProjectionAsync(CategoryMapper.Projection,
            pageSize: request.PageSize, pageIndex: Math.Max(1, request.PageIndex),
            includeTotal: includeTotal, splitQueryOptions: SplitQueryThresholdOptions.Default,
            cancellationToken: ct).ConfigureAwait(ConfigureAwaitOptions.None);

        return new PagedResponse<CategoryDto>
        {
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            Data = data,
            Total = total
        };
    }
}
