using Microsoft.AspNetCore.Mvc;
using Microsoft.FeatureManagement;
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
            [FromServices] IVariantFeatureManager featureManager,
            HttpContext httpContext,
            CancellationToken ct) =>
        {
            // GR-19/GR-20: SemanticSearch gates one mode, not the whole route, so the check is here rather
            // than in a RequireFeature endpoint filter - keyword search must keep answering while the flag
            // is off. 404, not 403, for the same reason the filter uses it: a disabled surface looks absent.
            if (mode == SearchMode.Semantic
                && !await featureManager.IsEnabledAsync(TaskFlowFeatures.SemanticSearch, ct))
            {
                return Results.NotFound();
            }

            if (maxResults <= 0 || maxResults > 50) maxResults = 10;

            var tenantClaim = httpContext.User.FindFirst("tenant_id")?.Value;
            Guid? tenantId = Guid.TryParse(tenantClaim, out var tid) ? tid : null;

            var results = await searchService.SearchTaskItemsAsync(query, mode, tenantId, maxResults, ct);
            return Results.Ok(results);
        }).WithName("SearchTasks");

        return app;
    }
}
