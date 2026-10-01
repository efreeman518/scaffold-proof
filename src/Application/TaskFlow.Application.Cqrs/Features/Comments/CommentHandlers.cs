using EF.Tenancy;
using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Cqrs.Shared;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Cqrs.Features.Comments;

/// <summary>Handles search comments work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class SearchCommentsHandler(
    IRequestContext<string, Guid?> requestContext,
    ICommentRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<SearchCommentsQuery, PagedResponse<CommentDto>>
{
    /// <summary>Handles search comments requests and returns the application result.</summary>
    public async Task<PagedResponse<CommentDto>> HandleAsync(SearchCommentsQuery query, CancellationToken ct = default)
    {
        var request = query.Request;
        request.Filter = tenantBoundaryValidator.EnforceTenantFilter(request.Filter, requestContext.TenantId, requestContext.Roles, "CommentSearch");
        return await repoQuery.SearchCommentsAsync(request, query.IncludeTotal, ct);
    }
}

/// <summary>Handles get comment by ID work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class GetCommentByIdHandler(
    IRequestContext<string, Guid?> requestContext,
    ICommentRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<GetCommentByIdQuery, Result<DefaultResponse<CommentDto>>>
{
    /// <summary>Handles get comment by ID requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<CommentDto>>> HandleAsync(GetCommentByIdQuery query, CancellationToken ct = default)
    {
        var entity = await repoQuery.GetCommentAsync(CommentId.From(query.Id), ct);
        if (entity is null) return Result<DefaultResponse<CommentDto>>.None();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Comment:Get", nameof(Comment), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(boundary.ErrorMessage!);

        return HandlerHelpers.Success(entity.ToDto());
    }
}
