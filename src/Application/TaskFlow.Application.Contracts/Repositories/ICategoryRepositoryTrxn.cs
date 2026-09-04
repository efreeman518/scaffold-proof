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
    /// Clears <c>TaskItem.CategoryId</c> for every task of the current tenant that references the category,
    /// as a set-based update. Required before deleting a category: the composite FK (TenantId, CategoryId)
    /// cannot cascade to SetNull (D-022). Returns the number of detached tasks.
    /// </summary>
    Task<int> ClearCategoryFromTaskItemsAsync(CategoryId categoryId, CancellationToken ct = default);
}
