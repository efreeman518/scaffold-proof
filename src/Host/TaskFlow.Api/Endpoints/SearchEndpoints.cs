using Microsoft.AspNetCore.Mvc;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Contracts;
using TaskFlow.Infrastructure.AI.Search;

namespace TaskFlow.Api.Endpoints;

/// <summary>Maps search HTTP routes to the selected application implementation and API contract metadata.</summary>
public static class SearchEndpoints
{
    /// <summary>Registers search routes, handlers, and response metadata.</summary>
    public static IEndpointRouteBuilder MapSearchEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/search").WithTags("Search");

        group.MapGet("/tasks", async (
            [FromQuery] string query,
            [FromQuery] SearchMode mode,
            [FromQuery] int maxResults,
            [FromServices] ITaskFlowSearchService searchService,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            if (maxResults <= 0 || maxResults > 50) maxResults = 10;

            var tenantClaim = httpContext.User.FindFirst("tenant_id")?.Value;
            Guid? tenantId = Guid.TryParse(tenantClaim, out var tid) ? tid : null;

            var results = await searchService.SearchTaskItemsAsync(query, mode, tenantId, maxResults, ct);
            return Results.Ok(results);
        })
        // GR-19/GR-20: SemanticSearch gates one mode, not the route. Keyword search keeps answering while the
        // flag is off; a Semantic request gets 404, because a disabled surface should look absent rather than
        // forbidden. Same filter as the TaskViews and Export gates, narrowed to the requests it applies to.
        .RequireFeature(
            TaskFlowFeatures.SemanticSearch,
            context => context.Arguments.OfType<SearchMode>().Contains(SearchMode.Semantic))
        .WithName("SearchTasks");

        return app;
    }
}
