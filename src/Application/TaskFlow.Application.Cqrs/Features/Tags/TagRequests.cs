using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using TaskFlow.Application.Models;

namespace TaskFlow.Application.Cqrs.Features.Tags;

/// <summary>Carries search tags query CQRS data between endpoints and handlers.</summary>
public sealed record SearchTagsQuery(SearchRequest<TagSearchFilter> Request, bool IncludeTotal = false)
    : IQuery<PagedResponse<TagDto>>;

/// <summary>Carries get tag by ID query CQRS data between endpoints and handlers.</summary>
public sealed record GetTagByIdQuery(Guid Id)
    : IQuery<Result<DefaultResponse<TagDto>>>;

/// <summary>Carries create tag command CQRS data between endpoints and handlers.</summary>
public sealed record CreateTagCommand(DefaultRequest<TagDto> Request)
    : ICommand<Result<DefaultResponse<TagDto>>>;

/// <summary>Carries update tag command CQRS data between endpoints and handlers.</summary>
public sealed record UpdateTagCommand(DefaultRequest<TagDto> Request, long? ExpectedVersion)
    : ICommand<Result<DefaultResponse<TagDto>>>;

/// <summary>Carries delete tag command CQRS data between endpoints and handlers.</summary>
public sealed record DeleteTagCommand(Guid Id, long? ExpectedVersion)
    : ICommand<Result>;
