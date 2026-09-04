using EF.AspNetCore;
using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using TaskFlow.Api.Endpoints.Shared;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Cqrs.Features.Tags;
using TaskFlow.Application.Models;

namespace TaskFlow.Api.Endpoints.Cqrs;

/// <summary>Maps tag CQRS HTTP routes to CQRS handlers and API contract metadata.</summary>
public static class TagCqrsEndpoints
{
    private static bool _problemDetailsIncludeStackTrace;

    /// <summary>Registers tag CQRS routes, handlers, and response metadata.</summary>
    public static IEndpointRouteBuilder MapTagCqrsEndpoints(this IEndpointRouteBuilder group, bool problemDetailsIncludeStackTrace)
    {
        _problemDetailsIncludeStackTrace = problemDetailsIncludeStackTrace;

        var g = group.MapGroup("/tags").WithTags("Tags")
            .AddEndpointFilter<ETagEndpointFilter>();

        g.MapPost("/search", Search)
            .Produces<PagedResponse<TagDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithSummary("Search Tags with paging, filters, and sorts");

        g.MapGet("/{id:guid}", GetById)
            .Produces<DefaultResponse<TagDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Get a single Tag");

        g.MapPost("/", Create)
            .Produces<DefaultResponse<TagDto>>(StatusCodes.Status201Created)
            .Produces<DefaultResponse<TagDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("Create a new Tag");

        g.MapPut("/{id:guid}", Update)
            .RequireIfMatch()
            .Produces<DefaultResponse<TagDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Update an existing Tag");

        g.MapDelete("/{id:guid}", Delete)
            .RequireIfMatch()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .WithSummary("Delete a Tag");

        return group;
    }

    /// <summary>Handles search requests and returns a paged application response.</summary>
    private static async Task<IResult> Search(
        [FromServices] IRequestHandler<SearchTagsQuery, PagedResponse<TagDto>> handler,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SearchRequest<TagSearchFilter>? request,
        CancellationToken ct,
        [FromQuery] bool includeTotal = false)
    {
        var search = request ?? new SearchRequest<TagSearchFilter>();
        var guard = SearchRequestGuard.Validate(search.PageSize);
        if (guard is not null) return guard;

        return TypedResults.Ok(await handler.HandleAsync(new SearchTagsQuery(search, includeTotal), ct));
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    private static async Task<IResult> GetById(
        [FromServices] IRequestHandler<GetTagByIdQuery, Result<DefaultResponse<TagDto>>> handler,
        Guid id,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetTagByIdQuery(id), ct);
        return result.Match<IResult>(
            response => TypedResults.Ok(response),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, statusCodeOverride: StatusCodes.Status400BadRequest)),
            () => TypedResults.NotFound(id));
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    private static async Task<IResult> Create(
        HttpContext httpContext,
        [FromServices] IRequestHandler<CreateTagCommand, Result<DefaultResponse<TagDto>>> handler,
        [FromBody] DefaultRequest<TagDto> request,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new CreateTagCommand(request), ct);
        return result.Match<IResult>(
            response => response.IsReplay
                ? TypedResults.Ok(response)
                : TypedResults.Created($"{httpContext.Request.Path}/{response.Item?.Id}", response),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, traceId: httpContext.TraceIdentifier,
                includeStackTrace: _problemDetailsIncludeStackTrace)));
    }

    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    private static async Task<IResult> Update(
        HttpContext httpContext,
        [FromServices] IRequestHandler<UpdateTagCommand, Result<DefaultResponse<TagDto>>> handler,
        Guid id,
        IfMatch ifMatch,
        [FromBody] DefaultRequest<TagDto> request,
        CancellationToken ct)
    {
        if (request.Item.Id != null && request.Item.Id != id)
            return TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponse(
                statusCodeOverride: StatusCodes.Status400BadRequest,
                message: $"{ErrorConstants.ERROR_URL_BODY_ID_MISMATCH}: {id} <> {request.Item.Id}"));

        var result = await handler.HandleAsync(new UpdateTagCommand(request, ifMatch.ExpectedVersion), ct);
        return result.Match(
            response => response.Item is null ? Results.NotFound(id) : TypedResults.Ok(response),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, traceId: httpContext.TraceIdentifier,
                includeStackTrace: _problemDetailsIncludeStackTrace)));
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    private static async Task<IResult> Delete(
        HttpContext httpContext,
        [FromServices] IRequestHandler<DeleteTagCommand, Result> handler,
        Guid id,
        IfMatch ifMatch,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new DeleteTagCommand(id, ifMatch.ExpectedVersion), ct);
        return result.Match<IResult>(
            () => TypedResults.NoContent(),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, traceId: httpContext.TraceIdentifier,
                includeStackTrace: _problemDetailsIncludeStackTrace)));
    }
}
