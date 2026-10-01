using EF.Data;
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
    // On a relational store the detach is a set-based ExecuteUpdate, so it runs with the delete's save in one
    // transaction under the execution strategy (ResilientTransaction): a failed or lost save rolls the detach back.
    // The action holds no read, so a strategy re-run sends the same detach and delete. The tenant query filter scopes
    // the update to the context tenant.
    public Task DeleteCategoryAsync(Category category, CancellationToken ct = default)
    {
        if (!DB.Database.IsRelational())
            return DetachAndDeleteAsync(category, ct);

        return ResilientTransaction.New(DB).ExecuteAsync(token => DetachAndDeleteAsync(category, token), ct);
    }

    /// <summary>Detaches the category's tasks, deletes the category and saves once with the Throw policy.</summary>
    private async Task DetachAndDeleteAsync(Category category, CancellationToken ct)
    {
        var tasks = DB.Set<TaskItem>().Where(t => t.CategoryId == category.Id);
        if (DB.Database.IsRelational())
        {
            await tasks
                // D-073: the detach moves each task's Version, so a PUT holding the task's pre-delete ETag answers 412
                // instead of restoring the dangling category id.
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.CategoryId, (CategoryId?)null).StampModified(DB.Clock.GetUtcNow()), ct)
                .ConfigureAwait(ConfigureAwaitOptions.None);
        }
        else
        {
            // The InMemory test provider has no ExecuteUpdate; clear through the tracked aggregate, in the same save.
            foreach (var task in await tasks.ToListAsync(ct).ConfigureAwait(ConfigureAwaitOptions.None))
            {
                task.Update(categoryId: CategoryId.From(Guid.Empty));
            }
        }

        Delete(category);
        await SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct).ConfigureAwait(ConfigureAwaitOptions.None);
    }
}
