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

/// <summary>
/// Read-side TaskItem repository. It uses the no-tracking query DbContext and projects search
/// results server-side so list endpoints avoid hydrating child collections.
/// </summary>
public class TaskItemRepositoryQuery(TaskFlowDbContextQuery db)
    : TaskFlowRepositoryQuery<TaskItem, TaskItemId>(db), ITaskItemRepositoryQuery
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<TaskItem?> GetTaskItemAsync(TaskItemId id, CancellationToken ct = default)
    {
        var includesList = new List<Expression<Func<IQueryable<TaskItem>, IIncludableQueryable<TaskItem, object?>>>>
        {
            q => q.Include(t => t.Category),
            q => q.Include(t => t.Comments),
            q => q.Include(t => t.ChecklistItems),
            q => q.Include(t => t.TaskItemTags).ThenInclude(tt => tt.Tag),
            q => q.Include(t => t.SubTasks)
        };

        return await GetEntityAsync(
            false,
            filter: t => t.Id == id,
            splitQueryThresholdOptions: SplitQueryThresholdOptions.Default,
            includes: [.. includesList],
            cancellationToken: ct
        ).ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <summary>
    /// Applies search filters, stable default ordering, include hints, and the lean search projection
    /// used by task list pages.
    /// </summary>
    public async Task<PagedResponse<TaskItemDto>> SearchTaskItemsAsync(SearchRequest<TaskItemSearchFilter> request, CancellationToken ct = default)
    {
        var q = DB.Set<TaskItem>().ComposeIQueryable(false);

        // ordering
        if (request.Sorts?.Any() ?? false)
        {
            q = ((IOrderedQueryable<TaskItem>)q.OrderBy(request.Sorts)).ThenBy(e => e.Id);
        }
        else
        {
            q = q.OrderBy(e => e.Title).ThenBy(e => e.Id);
        }

        // filtering
        var filter = request.Filter;
        if (filter is not null)
        {
            var searchTerm = filter.SearchTerm?.Trim();
            if (!string.IsNullOrWhiteSpace(searchTerm))
                q = q.Where(e => e.Title.Contains(searchTerm));

            if (filter.Status.HasValue)
            {
                var status = filter.Status.Value;
                q = q.Where(e => e.Status == status);
            }

            if (filter.Priority.HasValue)
            {
                var priority = filter.Priority.Value;
                q = q.Where(e => e.Priority == priority);
            }

            if (filter.CategoryId.HasValue)
            {
                var categoryId = DomainId.From<CategoryId>(filter.CategoryId.Value);
                q = q.Where(e => e.CategoryId == categoryId);
            }

            if (filter.ParentTaskItemId.HasValue)
            {
                var parentTaskItemId = DomainId.From<TaskItemId>(filter.ParentTaskItemId.Value);
                q = q.Where(e => e.ParentTaskItemId == parentTaskItemId);
            }

            if (filter.TenantId.HasValue)
            {
                var tenantId = DomainId.From<TenantId>(filter.TenantId.Value);
                q = q.Where(e => e.TenantId == tenantId);
            }

            if (filter.DueBefore.HasValue)
            {
                var dueBefore = filter.DueBefore.Value;
                q = q.Where(e => e.DueDate != null && e.DueDate <= dueBefore);
            }

            if (filter.DueAfter.HasValue)
            {
                var dueAfter = filter.DueAfter.Value;
                q = q.Where(e => e.DueDate != null && e.DueDate >= dueAfter);
            }

            if (filter.IsOverdue.HasValue && filter.IsOverdue.Value)
                q = q.Where(e => e.DueDate != null && e.DueDate < DateTimeOffset.UtcNow && e.CompletedDate == null);
        }

        // includes for SplitQuery
        var includesList = new List<Expression<Func<IQueryable<TaskItem>, IIncludableQueryable<TaskItem, object?>>>>
        {
            q => q.Include(t => t.Category),
            q => q.Include(t => t.TaskItemTags).ThenInclude(tt => tt.Tag)
        };

        (var data, var total) = await q.QueryPageProjectionAsync(TaskItemMapper.ProjectorSearch,
            pageSize: request.PageSize, pageIndex: Math.Max(1, request.PageIndex),
            includeTotal: true, splitQueryOptions: SplitQueryThresholdOptions.Default,
            includes: [.. includesList], cancellationToken: ct).ConfigureAwait(ConfigureAwaitOptions.None);

        return new PagedResponse<TaskItemDto>
        {
            PageIndex = request.PageIndex,
            PageSize = request.PageSize,
            Data = data,
            Total = total
        };
    }
}
