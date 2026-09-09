using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using TaskFlow.Application.Models;

namespace TaskFlow.Application.Cqrs.Features.Comments;

/// <summary>Carries search comments query CQRS data between endpoints and handlers.</summary>
public sealed record SearchCommentsQuery(SearchRequest<CommentSearchFilter> Request, bool IncludeTotal = false)
    : IQuery<PagedResponse<CommentDto>>;

/// <summary>Carries get comment by ID query CQRS data between endpoints and handlers.</summary>
public sealed record GetCommentByIdQuery(Guid Id)
    : IQuery<Result<DefaultResponse<CommentDto>>>;
