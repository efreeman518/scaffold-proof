using EF.AspNetCore;
using EF.Common.Contracts;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using TaskFlow.Api.Endpoints.Shared;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Api.Endpoints;

/// <summary>Maps attachment HTTP routes to the selected application implementation and API contract metadata.</summary>
public static class AttachmentEndpoints
{
    private static bool _problemDetailsIncludeStackTrace;

    /// <summary>Registers attachment routes, handlers, and response metadata.</summary>
    public static IEndpointRouteBuilder MapAttachmentEndpoints(this IEndpointRouteBuilder group, bool problemDetailsIncludeStackTrace)
    {
        _problemDetailsIncludeStackTrace = problemDetailsIncludeStackTrace;

        var g = group.MapGroup("/attachments").WithTags("Attachments")
            .AddEndpointFilter<ETagEndpointFilter>();

        g.MapPost("/search", Search)
            .Produces<PagedResponse<AttachmentDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithSummary("Search Attachments with paging, filters, and sorts");

        g.MapGet("/{id:guid}", GetById)
            .Produces<DefaultResponse<AttachmentDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Get a single Attachment");

        g.MapPost("/", Create)
            .Produces<DefaultResponse<AttachmentDto>>(StatusCodes.Status201Created)
            .Produces<DefaultResponse<AttachmentDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("Create a new Attachment");

        g.MapPost("/upload", Upload)
            .Produces<DefaultResponse<AttachmentDto>>(StatusCodes.Status201Created)
            .Produces<DefaultResponse<AttachmentDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("Upload a file Attachment")
            .DisableAntiforgery();

        g.MapPut("/{id:guid}", Update)
            .RequireIfMatch()
            .Produces<DefaultResponse<AttachmentDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Update an existing Attachment");

        g.MapDelete("/{id:guid}", Delete)
            .RequireIfMatch()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .WithSummary("Delete an Attachment");

        return group;
    }

    /// <summary>Handles search requests and returns a paged application response.</summary>
    private static async Task<IResult> Search(
        [FromServices] IAttachmentService service,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SearchRequest<AttachmentSearchFilter>? request,
        CancellationToken ct,
        [FromQuery] bool includeTotal = false)
    {
        var search = request ?? new SearchRequest<AttachmentSearchFilter>();
        var guard = SearchRequestGuard.Validate(search.PageSize);
        if (guard is not null) return guard;

        return TypedResults.Ok(await service.SearchAsync(search, includeTotal, ct));
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    private static async Task<IResult> GetById(
        [FromServices] IAttachmentService service, Guid id, CancellationToken ct)
    {
        var result = await service.GetAsync(id, ct);
        return result.Match<IResult>(
            response => TypedResults.Ok(response),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, statusCodeOverride: StatusCodes.Status400BadRequest)),
            () => TypedResults.NotFound(id));
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    private static async Task<IResult> Create(
        HttpContext httpContext,
        [FromServices] IAttachmentService service,
        [FromBody] DefaultRequest<AttachmentDto> request,
        CancellationToken ct)
    {
        var result = await service.CreateAsync(request, ct);
        return result.Match<IResult>(
            response => response.IsReplay
                ? TypedResults.Ok(response)
                : TypedResults.Created($"{httpContext.Request.Path}/{response.Item?.Id}", response),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, traceId: httpContext.TraceIdentifier,
                includeStackTrace: _problemDetailsIncludeStackTrace)));
    }

    /// <summary>Uploads upload to the configured storage backend and returns metadata.</summary>
    private static async Task<IResult> Upload(
        HttpContext httpContext,
        IFormFile file,
        [FromForm] AttachmentOwnerType ownerType,
        [FromForm] Guid ownerId,
        [FromServices] IAttachmentService service,
        CancellationToken ct,
        [FromForm] Guid? id = null)
    {
        await using var stream = file.OpenReadStream();
        var result = await service.UploadAsync(
            stream, file.FileName, file.ContentType, file.Length,
            ownerType, ownerId, id, ct);
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
        [FromServices] IAttachmentService service,
        Guid id,
        IfMatch ifMatch,
        [FromBody] DefaultRequest<AttachmentDto> request,
        CancellationToken ct)
    {
        if (request.Item.Id != null && request.Item.Id != id)
            return TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponse(
                statusCodeOverride: StatusCodes.Status400BadRequest,
                message: $"{ErrorConstants.ERROR_URL_BODY_ID_MISMATCH}: {id} <> {request.Item.Id}"));

        var result = await service.UpdateAsync(request, ifMatch.ExpectedVersion, ct);
        return result.Match(
            response => response.Item is null ? Results.NotFound(id) : TypedResults.Ok(response),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, traceId: httpContext.TraceIdentifier,
                includeStackTrace: _problemDetailsIncludeStackTrace)));
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    private static async Task<IResult> Delete(
        HttpContext httpContext,
        [FromServices] IAttachmentService service, Guid id, IfMatch ifMatch, CancellationToken ct)
    {
        var result = await service.DeleteAsync(id, ifMatch.ExpectedVersion, ct);
        return result.Match<IResult>(
            () => TypedResults.NoContent(),
            errors => TypedResults.Problem(
                ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                    errors: errors, traceId: httpContext.TraceIdentifier,
                    includeStackTrace: _problemDetailsIncludeStackTrace)));
    }
}
