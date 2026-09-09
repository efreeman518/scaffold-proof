using EF.Common.Contracts;
using EF.Data.Contracts;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Contracts.Repositories;

/// <summary>Persists and queries i category data through infrastructure storage contracts.</summary>
public interface ICategoryRepositoryQuery : IRepositoryQuery<Category, CategoryId>
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<Category?> GetCategoryAsync(CategoryId id, CancellationToken ct = default);
    /// <summary>Full active-category list for /task-metadata, hard-capped at <paramref name="max"/>.</summary>
    Task<IReadOnlyList<CategoryDto>> GetActiveCategoriesAsync(int max, CancellationToken ct = default);
    /// <summary>Searches search categories and returns filtered results for callers.</summary>
    Task<PagedResponse<CategoryDto>> SearchCategoriesAsync(SearchRequest<CategorySearchFilter> request, bool includeTotal = false, CancellationToken ct = default);
}
