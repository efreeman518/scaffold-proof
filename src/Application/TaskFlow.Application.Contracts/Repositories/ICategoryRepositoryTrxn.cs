using EF.Data.Contracts;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Contracts.Repositories;

/// <summary>Persists and queries i category data through infrastructure storage contracts.</summary>
public interface ICategoryRepositoryTrxn : IRepositoryTrxn<Category, CategoryId>
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<Category?> GetCategoryAsync(CategoryId id, CancellationToken ct = default);

    /// <summary>
    /// Deletes the category as one unit of work with one <c>OptimisticConcurrencyWinner.Throw</c> save: first clears
    /// <c>TaskItem.CategoryId</c> for every task of the current tenant that references it (the composite FK
    /// (TenantId, CategoryId) cannot cascade to SetNull, D-022), then removes the row. A failed or lost save leaves the
    /// tasks' references in place, so a fresh-read retry (D-073) can run it again.
    /// </summary>
    Task DeleteCategoryAsync(Category category, CancellationToken ct = default);
}
