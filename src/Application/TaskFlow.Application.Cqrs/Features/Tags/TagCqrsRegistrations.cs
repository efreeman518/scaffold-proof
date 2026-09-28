using EF.Common.Contracts;
using EF.CQRS.DependencyInjection;
using TaskFlow.Application.Models;

namespace TaskFlow.Application.Cqrs.Features.Tags;

/// <summary>Provides tag CQRS registrations behavior for the Features Tags layer.</summary>
internal static class TagCqrsRegistrations
{
    public static IReadOnlyList<RequestHandlerRegistration> Registrations { get; } =
    [
        new(typeof(SearchTagsQuery), typeof(PagedResponse<TagDto>), typeof(SearchTagsHandler)),
        new(typeof(GetTagByIdQuery), typeof(Result<DefaultResponse<TagDto>>), typeof(GetTagByIdHandler)),
        new(typeof(CreateTagCommand), typeof(Result<DefaultResponse<TagDto>>), typeof(CreateTagHandler)),
        new(typeof(UpdateTagCommand), typeof(Result<DefaultResponse<TagDto>>), typeof(UpdateTagHandler)),
        new(typeof(DeleteTagCommand), typeof(Result), typeof(DeleteTagHandler)),
    ];
}
