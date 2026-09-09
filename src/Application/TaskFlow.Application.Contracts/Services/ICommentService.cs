using EF.Common.Contracts;
using TaskFlow.Application.Models;

namespace TaskFlow.Application.Contracts.Services;

/// <summary>Coordinates i comment application use cases with validation, tenant checks, repositories, and response shaping.</summary>
public interface ICommentService
{
    /// <summary>Searches search and returns filtered results for callers.</summary>
    Task<PagedResponse<CommentDto>> SearchAsync(SearchRequest<CommentSearchFilter> request, bool includeTotal = false, CancellationToken ct = default);
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<Result<DefaultResponse<CommentDto>>> GetAsync(Guid id, CancellationToken ct = default);
}
