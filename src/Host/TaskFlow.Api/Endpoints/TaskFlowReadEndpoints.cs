using EF.Common.Contracts;
using Microsoft.AspNetCore.Mvc;
using System.Buffers;
using System.Diagnostics;
using System.Text.Json;
using TaskFlow.Api.Endpoints.Shared;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Application.Models.Reads;
using TaskFlow.Application.Models.Serialization;
using TaskFlow.Observability.Meters;

namespace TaskFlow.Api.Endpoints;

/// <summary>
/// Aggregate read endpoints. These are style-agnostic: they project data and hold no domain behavior,
/// so both the Service and CQRS maps get the same routes from one place instead of two near-identical
/// copies. Mapped beside MapSearchEndpoints for that reason.
/// </summary>
public static class TaskFlowReadEndpoints
{
    private const string NdJsonContentType = "application/x-ndjson";

    /// <summary>Pending (unflushed) byte ceiling before the export writer is drained to the socket (64 KB).</summary>
    private const int FlushThresholdBytes = 64 * 1024;

    /// <summary>Registers summary, metadata, and export routes.</summary>
    public static IEndpointRouteBuilder MapTaskFlowReadEndpoints(this IEndpointRouteBuilder app)
    {
        var tasks = app.MapGroup("/task-items").WithTags("TaskItems");

        tasks.MapGet("/summary", GetSummary)
            .WithName("GetTaskItemSummary")
            .Produces<TaskItemSummaryDto>(StatusCodes.Status200OK)
            .WithSummary("Tenant task counts by status, overdue, and total in one round trip");

        tasks.MapGet("/export", Export)
            .WithName("ExportTaskItems")
            .Produces<TaskItemExportDto>(StatusCodes.Status200OK, NdJsonContentType)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status404NotFound)
            // Own budget: an export holds a connection for as long as the tenant has rows, so it must not
            // spend the tenant's interactive allowance.
            .WithMetadata(new ExportRateLimitPolicy())
            .RequireRateLimiting(ExportRateLimitPolicy.PolicyName)
            .RequireFeature(TaskFlowFeatures.Export)
            .WithSummary("Stream the tenant's tasks as newline-delimited JSON, resumable by afterId");

        app.MapGet("/task-metadata", GetMetadata)
            .WithName("GetTaskMetadata")
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
        [FromServices] StreamingMeter meter,
        CancellationToken ct,
        [FromQuery] Guid? afterId = null,
        [FromQuery] int? batchSize = null)
    {
        var size = batchSize ?? PageSizeLimits.Default;
        var guard = SearchRequestGuard.Validate(size);
        if (guard is not null) return guard;

        httpContext.Response.ContentType = NdJsonContentType;

        var stopwatch = Stopwatch.StartNew();
        var written = 0;
        var completed = false;
        // Hot path (D-047/D-048): one Utf8JsonWriter over the response PipeWriter for the whole stream,
        // Reset between rows, writing through the source-generated TaskItemExportDto metadata. The previous
        // SerializeAsync-per-row built a writer, rented a buffer and drove an async state machine for every
        // row, then awaited a Stream write in between; a 100k-row tenant paid all of that 100k times.
        // Utf8JsonWriter is synchronous over IBufferWriter, so the only awaits left are the real flushes.
        var body = httpContext.Response.BodyWriter;
        var writer = new Utf8JsonWriter(body, new JsonWriterOptions { SkipValidation = true });
        try
        {
            await foreach (var row in reads.StreamTaskItemExportAsync(afterId, size, ct))
            {
                writer.Reset(body);
                JsonSerializer.Serialize(writer, row, TaskFlowJsonContext.Default.TaskItemExportDto);
                body.Write(NewLine);
                written++;

                // Flush on a full buffer or at the batch boundary, whichever comes first: the byte ceiling
                // bounds memory on wide rows, the batch boundary keeps a client on narrow rows seeing
                // progress. FlushAsync observes ct, so a disconnected client stops the enumeration - and the
                // database work behind it - instead of paging the rest of the tenant into a void.
                if (writer.BytesPending >= FlushThresholdBytes || written % size == 0)
                {
                    writer.Flush();
                    await body.FlushAsync(ct);
                }
            }

            writer.Flush();
            await body.FlushAsync(ct);
            completed = true;
        }
        finally
        {
            await writer.DisposeAsync();
            // Recorded in a finally so a client disconnect is measured too: an abandoned export still cost
            // the rows it produced, and a rising abandoned rate is the signal that clients are timing out.
            meter.RecordExport(written, stopwatch.Elapsed.TotalMilliseconds, completed);
        }

        // The response body is already written; Empty adds nothing further.
        return TypedResults.Empty;
    }

    /// <summary>NDJSON row separator. A static field, not a property that re-encodes on every row.</summary>
    private static readonly byte[] NewLine = [(byte)'\n'];
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
