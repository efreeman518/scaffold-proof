using EF.AspNetCore;
using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using TaskFlow.Api.Endpoints.Shared;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Cqrs.Features.ChecklistItems;
using TaskFlow.Application.Models;

namespace TaskFlow.Api.Endpoints.Cqrs;

/// <summary>Maps checklist item CQRS HTTP routes to CQRS handlers and API contract metadata.</summary>
public static class ChecklistItemCqrsEndpoints
{
    private static bool _problemDetailsIncludeStackTrace;

    /// <summary>Registers checklist item CQRS routes, handlers, and response metadata.</summary>
    public static IEndpointRouteBuilder MapChecklistItemCqrsEndpoints(this IEndpointRouteBuilder group, bool problemDetailsIncludeStackTrace)
    {
        _problemDetailsIncludeStackTrace = problemDetailsIncludeStackTrace;

        var g = group.MapGroup("/checklist-items").WithTags("ChecklistItems")
            .AddEndpointFilter<ETagEndpointFilter>();

        g.MapPost("/search", Search)
            .Produces<PagedResponse<ChecklistItemDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithSummary("Search ChecklistItems with paging, filters, and sorts");

        g.MapGet("/{id:guid}", GetById)
            .Produces<DefaultResponse<ChecklistItemDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Get a single ChecklistItem");

        return group;
    }

    /// <summary>Handles search requests and returns a paged application response.</summary>
    private static async Task<IResult> Search(
        [FromServices] IRequestHandler<SearchChecklistItemsQuery, PagedResponse<ChecklistItemDto>> handler,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SearchRequest<ChecklistItemSearchFilter>? request,
        CancellationToken ct,
        [FromQuery] bool includeTotal = false)
    {
        var search = request ?? new SearchRequest<ChecklistItemSearchFilter>();
        var guard = SearchRequestGuard.Validate(search.PageSize);
        if (guard is not null) return guard;

        return TypedResults.Ok(await handler.HandleAsync(new SearchChecklistItemsQuery(search, includeTotal), ct));
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    private static async Task<IResult> GetById(
        [FromServices] IRequestHandler<GetChecklistItemByIdQuery, Result<DefaultResponse<ChecklistItemDto>>> handler,
        Guid id,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetChecklistItemByIdQuery(id), ct);
        return result.Match<IResult>(
            response => TypedResults.Ok(response),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, statusCodeOverride: StatusCodes.Status400BadRequest)),
            () => TypedResults.NotFound(id));
    }
}
