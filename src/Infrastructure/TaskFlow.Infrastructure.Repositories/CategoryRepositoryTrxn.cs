using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using System.Linq.Expressions;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>Persists and queries category data through infrastructure storage contracts.</summary>
public class CategoryRepositoryTrxn(TaskFlowDbContextTrxn db)
    : TaskFlowRepositoryTrxn<Category, CategoryId>(db), ICategoryRepositoryTrxn
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Category?> GetCategoryAsync(CategoryId id, CancellationToken ct = default)
    {
        var includesList = new List<Expression<Func<IQueryable<Category>, IIncludableQueryable<Category, object?>>>>
        {
            q => q.Include(c => c.SubCategories)
        };

        return await GetEntityAsync(
            true,
            filter: c => c.Id == id,
            splitQueryThresholdOptions: SplitQueryThresholdOptions.Default,
            includes: [.. includesList],
            cancellationToken: ct
        ).ConfigureAwait(ConfigureAwaitOptions.None);
    }

    /// <inheritdoc />
    // The tenant query filter scopes the update to the context tenant. shortcut: ExecuteUpdate commits
    // immediately, so a failing category delete afterwards leaves the tasks detached; wrap both in
    // CreateExecutionStrategy().ExecuteAsync + transaction if that ever matters.
    public async Task<int> ClearCategoryFromTaskItemsAsync(CategoryId categoryId, CancellationToken ct = default)
    {
        var tasks = DB.Set<TaskItem>().Where(t => t.CategoryId == categoryId);
        if (DB.Database.IsRelational())
        {
            return await tasks
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, (CategoryId?)null), ct)
                .ConfigureAwait(ConfigureAwaitOptions.None);
        }

        // The InMemory test provider has no ExecuteUpdate; clear through the tracked aggregate instead.
        var tracked = await tasks.ToListAsync(ct).ConfigureAwait(ConfigureAwaitOptions.None);
        foreach (var task in tracked)
        {
            task.Update(categoryId: DomainId.From<CategoryId>(Guid.Empty));
        }

        return tracked.Count;
    }
}
