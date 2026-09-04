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
using TaskFlow.Infrastructure.Data.Configurations;
using TaskFlow.Infrastructure.Data.Encryption;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// Read-side TaskItem repository. It uses the no-tracking query DbContext and projects search
/// results server-side so list endpoints avoid hydrating child collections.
/// </summary>
public class TaskItemRepositoryQuery(TaskFlowDbContextQuery db, ColumnEncryptionKeys encryptionKeys)
    : TaskFlowRepositoryQuery<TaskItem, TaskItemId>(db), ITaskItemRepositoryQuery
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    // Plain AsNoTracking rather than the package GetEntityAsync(tracking: false): that path uses
    // NoTrackingWithIdentityResolution, which EF Core 10 rejects when a JSON-mapped owned type
    // (RecurrencePattern) is loaded together with collection includes.
    public async Task<TaskItem?> GetTaskItemAsync(TaskItemId id, CancellationToken ct = default) =>
        await DB.Set<TaskItem>()
            .AsNoTracking()
            .AsSplitQuery()
            .Include(t => t.Category)
            .Include(t => t.Comments)
            .Include(t => t.ChecklistItems)
            .Include(t => t.TaskItemTags).ThenInclude(tt => tt.Tag)
            .Include(t => t.SubTasks)
            .FirstOrDefaultAsync(t => t.Id == id, ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);

    /// <inheritdoc />
    public async Task<TaskItem?> FindBySecureTokenAsync(string secureDeterministic, CancellationToken ct = default)
    {
        // Equality on the keyed HMAC shadow column (IX_TaskItem_TenantId_SecureDeterministicBlindIndex); the
        // ciphertext column is randomized and can never be compared directly.
        var blindIndex = BlindIndex.Compute(secureDeterministic, encryptionKeys.BlindIndexKey);
        return await DB.Set<TaskItem>()
            .Where(t => Microsoft.EntityFrameworkCore.EF.Property<byte[]>(t, TaskItemConfiguration.SecureDeterministicBlindIndex) == blindIndex)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(ConfigureAwaitOptions.None);
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
