using EF.Common.Contracts;
using TaskFlow.Application.Models;

namespace TaskFlow.Application.Contracts.Services;

/// <summary>Coordinates i checklist item application use cases with validation, tenant checks, repositories, and response shaping.</summary>
public interface IChecklistItemService
{
    /// <summary>Searches search and returns filtered results for callers.</summary>
    Task<PagedResponse<ChecklistItemDto>> SearchAsync(SearchRequest<ChecklistItemSearchFilter> request, bool includeTotal = false, CancellationToken ct = default);
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<Result<DefaultResponse<ChecklistItemDto>>> GetAsync(Guid id, CancellationToken ct = default);
}
