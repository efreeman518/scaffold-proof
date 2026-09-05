using Microsoft.AspNetCore.Mvc;
using System.Text;
using System.Text.Json;
using TaskFlow.Api.Endpoints.Shared;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Application.Models.Reads;

namespace TaskFlow.Api.Endpoints;

/// <summary>
/// Aggregate read endpoints. These are style-agnostic: they project data and hold no domain behavior,
/// so both the Service and CQRS maps get the same routes from one place instead of two near-identical
/// copies. Mapped beside MapSearchEndpoints for that reason.
/// </summary>
public static class TaskFlowReadEndpoints
{
    private const string NdJsonContentType = "application/x-ndjson";

    /// <summary>Registers summary, metadata, and export routes.</summary>
    public static IEndpointRouteBuilder MapTaskFlowReadEndpoints(this IEndpointRouteBuilder app)
    {
        var tasks = app.MapGroup("/task-items").WithTags("TaskItems");

        tasks.MapGet("/summary", GetSummary)
            .Produces<TaskItemSummaryDto>(StatusCodes.Status200OK)
            .WithSummary("Tenant task counts by status, overdue, and total in one round trip");

        tasks.MapGet("/export", Export)
            .Produces<TaskItemExportDto>(StatusCodes.Status200OK, NdJsonContentType)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            // Own budget: an export holds a connection for as long as the tenant has rows, so it must not
            // spend the tenant's interactive allowance.
            .WithMetadata(new ExportRateLimitPolicy())
            .RequireRateLimiting(ExportRateLimitPolicy.PolicyName)
            .WithSummary("Stream the tenant's tasks as newline-delimited JSON, resumable by afterId");

        app.MapGet("/task-metadata", GetMetadata)
            .WithTags("TaskItems")
            .Produces<TaskMetadataDto>(StatusCodes.Status200OK)
            .WithSummary("Full category and tag lists for pickers");

        return app;
    }

    /// <summary>Returns the tenant task summary.</summary>
    private static async Task<IResult> GetSummary(
        [FromServices] ITaskFlowReadService reads, CancellationToken ct) =>
        TypedResults.Ok(await reads.GetTaskItemSummaryAsync(ct));

    /// <summary>Returns the tenant's category and tag lists.</summary>
    private static async Task<IResult> GetMetadata(
        [FromServices] ITaskFlowReadService reads, CancellationToken ct) =>
        TypedResults.Ok(await reads.GetTaskMetadataAsync(ct));

    /// <summary>
    /// Streams export rows as NDJSON, flushing once per batch so a client sees progress instead of
    /// waiting for the whole tenant. The request token cancels the enumeration on disconnect, so a
    /// closed connection stops the database work instead of paging the rest of the tenant into a void.
    /// </summary>
    private static async Task<IResult> Export(
        HttpContext httpContext,
        [FromServices] ITaskFlowReadService reads,
        CancellationToken ct,
        [FromQuery] Guid? afterId = null,
        [FromQuery] int? batchSize = null)
    {
        var size = batchSize ?? PageSizeLimits.Default;
        var guard = SearchRequestGuard.Validate(size);
        if (guard is not null) return guard;

        var jsonOptions = httpContext.RequestServices
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>()
            .Value.SerializerOptions;

        httpContext.Response.ContentType = NdJsonContentType;

        var written = 0;
        await foreach (var row in reads.StreamTaskItemExportAsync(afterId, size, ct))
        {
            await JsonSerializer.SerializeAsync(httpContext.Response.Body, row, jsonOptions, ct);
            await httpContext.Response.Body.WriteAsync(NewLine, ct);

            if (++written % size == 0)
                await httpContext.Response.Body.FlushAsync(ct);
        }

        await httpContext.Response.Body.FlushAsync(ct);

        // The response body is already written; Empty adds nothing further.
        return TypedResults.Empty;
    }

    private static ReadOnlyMemory<byte> NewLine => Encoding.UTF8.GetBytes("\n");
}

/// <summary>
/// Reserves the "Export" rate-limit policy name on the streaming route. The policy is registered with
/// the distributed limiter work; the metadata is declared here so the route it belongs to is the thing
/// that names it.
/// </summary>
public sealed class ExportRateLimitPolicy
{
    public const string PolicyName = "Export";
}
