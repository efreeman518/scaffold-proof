using EF.Common.Contracts;
using EF.Data.Contracts;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Contracts.Repositories;

/// <summary>Persists and queries i comment data through infrastructure storage contracts.</summary>
public interface ICommentRepositoryQuery : IRepositoryQuery<Comment, CommentId>
{
    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<Comment?> GetCommentAsync(CommentId id, CancellationToken ct = default);
    /// <summary>Searches search comments and returns filtered results for callers.</summary>
    Task<PagedResponse<CommentDto>> SearchCommentsAsync(SearchRequest<CommentSearchFilter> request, bool includeTotal = false, CancellationToken ct = default);
}
