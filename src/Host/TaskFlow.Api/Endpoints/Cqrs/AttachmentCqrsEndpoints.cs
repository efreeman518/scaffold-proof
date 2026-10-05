using EF.AspNetCore;
using EF.AspNetCore.Concurrency;
using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using TaskFlow.Api.Endpoints.Shared;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Cqrs.Features.Attachments;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Api.Endpoints.Cqrs;

/// <summary>Maps attachment CQRS HTTP routes to CQRS handlers and API contract metadata.</summary>
public static class AttachmentCqrsEndpoints
{
    /// <summary>Registers attachment CQRS routes, handlers, and response metadata.</summary>
    public static IEndpointRouteBuilder MapAttachmentCqrsEndpoints(this IEndpointRouteBuilder group)
    {
        var g = group.MapGroup("/attachments").WithTags("Attachments")
            .WithETag();

        g.MapPost("/search", Search)
            .WithName("SearchAttachments")
            .Produces<PagedResponse<AttachmentDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithSummary("Search Attachments with paging, filters, and sorts");

        g.MapGet("/{id:guid}", GetById)
            .WithName("GetAttachment")
            .Produces<DefaultResponse<AttachmentDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Get a single Attachment");

        g.MapPost("/", Create)
            .WithName("CreateAttachment")
            .Produces<DefaultResponse<AttachmentDto>>(StatusCodes.Status201Created)
            .Produces<DefaultResponse<AttachmentDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("Create a new Attachment")
            .WithIdempotencyKey<AttachmentDto>(IdempotencyKeyFilter.AttachmentCreateScope);

        g.MapPost("/upload", Upload)
            .WithName("UploadAttachment")
            .Produces<DefaultResponse<AttachmentDto>>(StatusCodes.Status201Created)
            .Produces<DefaultResponse<AttachmentDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("Upload a file Attachment")
            .DisableAntiforgery();

        g.MapPut("/{id:guid}", Update)
            .WithName("UpdateAttachment")
            .RequireIfMatch()
            .Produces<DefaultResponse<AttachmentDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Update an existing Attachment");

        g.MapDelete("/{id:guid}", Delete)
            .WithName("DeleteAttachment")
            .RequireIfMatch()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .WithSummary("Delete an Attachment");

        return group;
    }

    /// <summary>Handles search requests and returns a paged application response.</summary>
    private static async Task<IResult> Search(
        [FromServices] IRequestHandler<SearchAttachmentsQuery, PagedResponse<AttachmentDto>> handler,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] SearchRequest<AttachmentSearchFilter>? request,
        CancellationToken ct,
        [FromQuery] bool includeTotal = false)
    {
        var search = request ?? new SearchRequest<AttachmentSearchFilter>();
        var guard = SearchRequestGuard.Validate(search.PageSize) ?? SearchRequestGuard.ValidateContentTypes(search.Filter?.ContentTypes);
        if (guard is not null) return guard;

        return TypedResults.Ok(await handler.HandleAsync(new SearchAttachmentsQuery(search, includeTotal), ct));
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    private static async Task<IResult> GetById(
        [FromServices] IRequestHandler<GetAttachmentByIdQuery, Result<DefaultResponse<AttachmentDto>>> handler,
        Guid id,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetAttachmentByIdQuery(id), ct);
        return result.Match<IResult>(
            response => TypedResults.Ok(response),
            errors => TypedResults.Problem(ProblemDetailsHelper.FromErrors(errors)),
            () => TypedResults.NotFound(id));
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    private static async Task<IResult> Create(
        HttpContext httpContext,
        [FromServices] IRequestHandler<CreateAttachmentCommand, Result<DefaultResponse<AttachmentDto>>> handler,
        [FromBody] DefaultRequest<AttachmentDto> request,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new CreateAttachmentCommand(request), ct);
        return result.Match<IResult>(
            response => response.IsReplay
                ? TypedResults.Ok(response)
                : TypedResults.Created($"{httpContext.Request.Path}/{response.Item?.Id}", response),
            errors => TypedResults.Problem(ProblemDetailsHelper.FromErrors(errors)));
    }

    /// <summary>Uploads upload to the configured storage backend and returns metadata.</summary>
    private static async Task<IResult> Upload(
        HttpContext httpContext,
        IFormFile file,
        [FromForm] AttachmentOwnerType ownerType,
        [FromForm] Guid ownerId,
        [FromServices] IRequestHandler<UploadAttachmentCommand, Result<DefaultResponse<AttachmentDto>>> handler,
        CancellationToken ct,
        [FromForm] Guid? id = null)
    {
        await using var stream = file.OpenReadStream();
        var result = await handler.HandleAsync(
            new UploadAttachmentCommand(stream, file.FileName, file.ContentType, file.Length, ownerType, ownerId, id),
            ct);
        return result.Match<IResult>(
            response => response.IsReplay
                ? TypedResults.Ok(response)
                : TypedResults.Created($"{httpContext.Request.Path}/{response.Item?.Id}", response),
            errors => TypedResults.Problem(ProblemDetailsHelper.FromErrors(errors)));
    }

    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    private static async Task<IResult> Update(
        HttpContext httpContext,
        [FromServices] IRequestHandler<UpdateAttachmentCommand, Result<DefaultResponse<AttachmentDto>>> handler,
        Guid id,
        IfMatch ifMatch,
        [FromBody] DefaultRequest<AttachmentDto> request,
        CancellationToken ct)
    {
        if (request.Item.Id != null && request.Item.Id != id)
            return TypedResults.Problem(ProblemDetailsHelper.Create(
                StatusCodes.Status400BadRequest,
                $"{ErrorConstants.ERROR_URL_BODY_ID_MISMATCH}: {id} <> {request.Item.Id}"));

        var result = await handler.HandleAsync(new UpdateAttachmentCommand(request, ifMatch.ExpectedVersion), ct);
        return result.Match(
            response => response.Item is null ? Results.NotFound(id) : TypedResults.Ok(response),
            errors => TypedResults.Problem(ProblemDetailsHelper.FromErrors(errors)));
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    private static async Task<IResult> Delete(
        HttpContext httpContext,
        [FromServices] IRequestHandler<DeleteAttachmentCommand, Result> handler,
        Guid id,
        IfMatch ifMatch,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new DeleteAttachmentCommand(id, ifMatch.ExpectedVersion), ct);
        return result.Match<IResult>(
            () => TypedResults.NoContent(),
            errors => TypedResults.Problem(ProblemDetailsHelper.FromErrors(errors)));
    }
}
