using EF.AspNetCore;
using EF.Common.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using TaskFlow.Api.Endpoints.Shared;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models;

namespace TaskFlow.Api.Endpoints;

/// <summary>Maps checklist item HTTP routes to the selected application implementation and API contract metadata.</summary>
public static class ChecklistItemEndpoints
{
    private static bool _problemDetailsIncludeStackTrace;

    /// <summary>Registers checklist item routes, handlers, and response metadata.</summary>
    public static IEndpointRouteBuilder MapChecklistItemEndpoints(this IEndpointRouteBuilder group, bool problemDetailsIncludeStackTrace)
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
        [FromServices] IChecklistItemService service,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SearchRequest<ChecklistItemSearchFilter>? request,
        CancellationToken ct,
        [FromQuery] bool includeTotal = false)
    {
        var search = request ?? new SearchRequest<ChecklistItemSearchFilter>();
        var guard = SearchRequestGuard.Validate(search.PageSize);
        if (guard is not null) return guard;

        return TypedResults.Ok(await service.SearchAsync(search, includeTotal, ct));
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    private static async Task<IResult> GetById(
        [FromServices] IChecklistItemService service, Guid id, CancellationToken ct)
    {
        var result = await service.GetAsync(id, ct);
        return result.Match<IResult>(
            response => TypedResults.Ok(response),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, statusCodeOverride: StatusCodes.Status400BadRequest)),
            () => TypedResults.NotFound(id));
    }
}
