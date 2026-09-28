using EF.Common.Contracts;
using EF.CQRS.DependencyInjection;
using TaskFlow.Application.Models;

namespace TaskFlow.Application.Cqrs.Features.Comments;

/// <summary>Provides comment CQRS registrations behavior for the Features Comments layer.</summary>
internal static class CommentCqrsRegistrations
{
    public static IReadOnlyList<RequestHandlerRegistration> Registrations { get; } =
    [
        new(typeof(SearchCommentsQuery), typeof(PagedResponse<CommentDto>), typeof(SearchCommentsHandler)),
        new(typeof(GetCommentByIdQuery), typeof(Result<DefaultResponse<CommentDto>>), typeof(GetCommentByIdHandler)),
    ];
}
