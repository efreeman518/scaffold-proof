using Microsoft.AspNetCore.Mvc;
using TaskFlow.Application.Contracts.Storage;

namespace TaskFlow.Api.Endpoints;

/// <summary>Maps task view HTTP routes to the selected application implementation and API contract metadata.</summary>
public static class TaskViewEndpoints
{
    /// <summary>Registers task view routes, handlers, and response metadata.</summary>
    public static IEndpointRouteBuilder MapTaskViewEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/task-views").WithTags("TaskViews");

        group.MapGet("/{id}", async (string id,
            [FromQuery] string tenantId,
            [FromServices] ITaskViewRepository repo,
            CancellationToken ct) =>
        {
            var result = await repo.GetAsync(id, tenantId, ct);
            return result is not null ? Results.Ok(result) : Results.NotFound();
        }).WithName("GetTaskView");

        group.MapGet("/", async (
            [FromQuery] string tenantId,
            [FromQuery] int? pageSize,
            [FromQuery] string? continuationToken,
            [FromServices] ITaskViewRepository repo,
            CancellationToken ct) =>
        {
            // The store continuation token is round-tripped; without it every request returned page one.
            var page = await repo.QueryByTenantAsync(tenantId, pageSize ?? 20, continuationToken, ct);
            return Results.Ok(page);
        }).WithName("GetTaskViews");

        return app;
    }
}
