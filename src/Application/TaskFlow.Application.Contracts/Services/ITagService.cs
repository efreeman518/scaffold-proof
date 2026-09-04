using EF.Common.Contracts;
using TaskFlow.Application.Models;

namespace TaskFlow.Application.Contracts.Services;

/// <summary>Coordinates i tag application use cases with validation, tenant checks, repositories, and response shaping.</summary>
public interface ITagService
{
    /// <summary>Searches search and returns filtered results for callers.</summary>
    Task<PagedResponse<TagDto>> SearchAsync(SearchRequest<TagSearchFilter> request, bool includeTotal = false, CancellationToken ct = default);
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<Result<DefaultResponse<TagDto>>> GetAsync(Guid id, CancellationToken ct = default);
    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    Task<Result<DefaultResponse<TagDto>>> CreateAsync(DefaultRequest<TagDto> request, CancellationToken ct = default);
    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    Task<Result<DefaultResponse<TagDto>>> UpdateAsync(DefaultRequest<TagDto> request, long? expectedVersion, CancellationToken ct = default);
    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    Task<Result> DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default);
}
