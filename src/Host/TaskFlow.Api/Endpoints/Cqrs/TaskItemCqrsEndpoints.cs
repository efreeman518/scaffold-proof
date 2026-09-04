using EF.AspNetCore;
using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using TaskFlow.Api.Endpoints.Shared;
using TaskFlow.Api.Filters;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Cqrs.Features.TaskItems;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;

namespace TaskFlow.Api.Endpoints.Cqrs;

/// <summary>Maps task item CQRS HTTP routes to CQRS handlers and API contract metadata.</summary>
public static class TaskItemCqrsEndpoints
{
    private static bool _problemDetailsIncludeStackTrace;

    /// <summary>Registers task item CQRS routes, handlers, and response metadata.</summary>
    public static IEndpointRouteBuilder MapTaskItemCqrsEndpoints(this IEndpointRouteBuilder group, bool problemDetailsIncludeStackTrace)
    {
        _problemDetailsIncludeStackTrace = problemDetailsIncludeStackTrace;

        var g = group.MapGroup("/task-items").WithTags("TaskItems")
            .AddEndpointFilter<ETagEndpointFilter>();

        g.MapPost("/search", Search)
            .Produces<CursorPage<TaskItemDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .WithSummary("Search TaskItems with keyset (cursor) paging, filters, and a sort mode");

        g.MapGet("/{id:guid}", GetById)
            .Produces<DefaultResponse<TaskItemDto>>(StatusCodes.Status200OK)
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Get a single TaskItem");

        g.MapPost("/", Create)
            .Produces<DefaultResponse<TaskItemDto>>(StatusCodes.Status201Created)
            .Produces<DefaultResponse<TaskItemDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("Create a new TaskItem (optional caller-supplied UUIDv7 id makes it idempotent)");

        g.MapPut("/{id:guid}", Update)
            .RequireIfMatch()
            .Produces<DefaultResponse<TaskItemDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Update an existing TaskItem");

        // PATCH exists in both styles now; its absence here previously 404'd the AI triage workflow
        // whenever the app ran in CQRS mode.
        g.MapPatch("/{id:guid}", Patch)
            .RequireIfMatch()
            .Produces<DefaultResponse<TaskItemDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Partially update a TaskItem (JSON merge patch - omitted fields are unchanged)");

        g.MapDelete("/{id:guid}", Delete)
            .RequireIfMatch()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .WithSummary("Delete a TaskItem");

        // Nested child routes. Comment, ChecklistItem, and the Tag association are internal to the
        // TaskItem aggregate, so they are mutated only through these sub-resource routes on the root
        // (GR-15) - there are no standalone /comments, /checklist-items, or /task-item-tags write
        // routes. Reads for comments/checklist-items still live on their own query endpoints.
        g.MapPost("/{id:guid}/comments", AddComment)
            .Produces<DefaultResponse<CommentDto>>(StatusCodes.Status201Created)
            .Produces<DefaultResponse<CommentDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("Add a Comment to a TaskItem");

        g.MapPut("/{id:guid}/comments/{commentId:guid}", UpdateComment)
            .RequireIfMatch()
            .Produces<DefaultResponse<CommentDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Update a Comment on a TaskItem");

        g.MapDelete("/{id:guid}/comments/{commentId:guid}", RemoveComment)
            .RequireIfMatch()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .WithSummary("Remove a Comment from a TaskItem");

        g.MapPost("/{id:guid}/checklist-items", AddChecklistItem)
            .Produces<DefaultResponse<ChecklistItemDto>>(StatusCodes.Status201Created)
            .Produces<DefaultResponse<ChecklistItemDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .WithSummary("Add a ChecklistItem to a TaskItem");

        g.MapPut("/{id:guid}/checklist-items/{checklistItemId:guid}", UpdateChecklistItem)
            .RequireIfMatch()
            .Produces<DefaultResponse<ChecklistItemDto>>()
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Update a ChecklistItem on a TaskItem");

        g.MapDelete("/{id:guid}/checklist-items/{checklistItemId:guid}", RemoveChecklistItem)
            .RequireIfMatch()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .WithSummary("Remove a ChecklistItem from a TaskItem");

        g.MapPost("/{id:guid}/tags/{tagId:guid}", AssociateTag)
            .Produces<DefaultResponse<TaskItemTagDto>>(StatusCodes.Status201Created)
            .Produces<DefaultResponse<TaskItemTagDto>>(StatusCodes.Status200OK)
            .ProducesValidationProblem()
            .ProducesProblem(StatusCodes.Status404NotFound)
            .WithSummary("Associate a Tag with a TaskItem");

        g.MapDelete("/{id:guid}/tags/{tagId:guid}", RemoveTag)
            .RequireIfMatch()
            .Produces(StatusCodes.Status204NoContent)
            .ProducesValidationProblem()
            .WithSummary("Remove a Tag association from a TaskItem");

        return group;
    }

    /// <summary>Handles search requests and returns a keyset page.</summary>
    private static async Task<IResult> Search(
        [FromServices] IRequestHandler<SearchTaskItemsQuery, CursorPage<TaskItemDto>> handler,
        [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] TaskItemCursorSearchRequest? request,
        CancellationToken ct)
    {
        var search = request ?? new TaskItemCursorSearchRequest();
        var guard = SearchRequestGuard.Validate(search.PageSize);
        if (guard is not null) return guard;

        return TypedResults.Ok(await handler.HandleAsync(new SearchTaskItemsQuery(search), ct));
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    private static async Task<IResult> GetById(
        [FromServices] IRequestHandler<GetTaskItemByIdQuery, Result<DefaultResponse<TaskItemDto>>> handler,
        Guid id,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetTaskItemByIdQuery(id), ct);
        return result.Match<IResult>(
            response => TypedResults.Ok(response),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, statusCodeOverride: StatusCodes.Status400BadRequest)),
            () => TypedResults.NotFound(id));
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    private static async Task<IResult> Create(
        HttpContext httpContext,
        [FromServices] IRequestHandler<CreateTaskItemCommand, Result<DefaultResponse<TaskItemDto>>> handler,
        [FromBody] DefaultRequest<TaskItemDto> request,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new CreateTaskItemCommand(request), ct);
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
        [FromServices] IRequestHandler<UpdateTaskItemCommand, Result<DefaultResponse<TaskItemDto>>> handler,
        Guid id,
        IfMatch ifMatch,
        [FromBody] DefaultRequest<TaskItemDto> request,
        CancellationToken ct)
    {
        if (request.Item.Id != null && request.Item.Id != id)
            return TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponse(
                statusCodeOverride: StatusCodes.Status400BadRequest,
                message: $"{ErrorConstants.ERROR_URL_BODY_ID_MISMATCH}: {id} <> {request.Item.Id}"));

        var result = await handler.HandleAsync(new UpdateTaskItemCommand(request, ifMatch.ExpectedVersion), ct);
        return result.Match(
            response => response.Item is null ? Results.NotFound(id) : TypedResults.Ok(response),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, traceId: httpContext.TraceIdentifier,
                includeStackTrace: _problemDetailsIncludeStackTrace)));
    }

    /// <summary>Applies a sparse partial update (JSON merge patch) to a TaskItem through the aggregate root.</summary>
    private static async Task<IResult> Patch(
        HttpContext httpContext,
        [FromServices] IRequestHandler<PatchTaskItemCommand, Result<DefaultResponse<TaskItemDto>>> handler,
        Guid id,
        IfMatch ifMatch,
        [FromBody] DefaultRequest<TaskItemPatchDto> request,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new PatchTaskItemCommand(id, request.Item, ifMatch.ExpectedVersion), ct);
        return result.Match(
            response => response.Item is null ? Results.NotFound(id) : TypedResults.Ok(response),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, traceId: httpContext.TraceIdentifier,
                includeStackTrace: _problemDetailsIncludeStackTrace)));
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    private static async Task<IResult> Delete(
        HttpContext httpContext,
        [FromServices] IRequestHandler<DeleteTaskItemCommand, Result> handler,
        Guid id,
        IfMatch ifMatch,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new DeleteTaskItemCommand(id, ifMatch.ExpectedVersion), ct);
        return result.Match<IResult>(
            () => TypedResults.NoContent(),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, traceId: httpContext.TraceIdentifier,
                includeStackTrace: _problemDetailsIncludeStackTrace)));
    }

    /// <summary>Adds a comment to the TaskItem aggregate through the root.</summary>
    private static async Task<IResult> AddComment(
        HttpContext httpContext,
        [FromServices] IRequestHandler<AddTaskItemCommentCommand, Result<DefaultResponse<CommentDto>>> handler,
        Guid id,
        [FromBody] DefaultRequest<CommentDto> request,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new AddTaskItemCommentCommand(id, request.Item), ct);
        return result.Match(
            response => response.Item is null
                ? Results.NotFound(id)
                : response.IsReplay
                    ? TypedResults.Ok(response)
                    : TypedResults.Created($"{httpContext.Request.Path}/{response.Item.Id}", response),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, traceId: httpContext.TraceIdentifier,
                includeStackTrace: _problemDetailsIncludeStackTrace)));
    }

    /// <summary>Updates a comment owned by the TaskItem aggregate.</summary>
    private static async Task<IResult> UpdateComment(
        HttpContext httpContext,
        [FromServices] IRequestHandler<UpdateTaskItemCommentCommand, Result<DefaultResponse<CommentDto>>> handler,
        Guid id,
        Guid commentId,
        IfMatch ifMatch,
        [FromBody] DefaultRequest<CommentDto> request,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(
            new UpdateTaskItemCommentCommand(id, commentId, request.Item, ifMatch.ExpectedVersion), ct);
        return result.Match(
            response => response.Item is null ? Results.NotFound(commentId) : TypedResults.Ok(response),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, traceId: httpContext.TraceIdentifier,
                includeStackTrace: _problemDetailsIncludeStackTrace)));
    }

    /// <summary>Removes a comment from the TaskItem aggregate through the root.</summary>
    private static async Task<IResult> RemoveComment(
        HttpContext httpContext,
        [FromServices] IRequestHandler<RemoveTaskItemCommentCommand, Result> handler,
        Guid id,
        Guid commentId,
        IfMatch ifMatch,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(
            new RemoveTaskItemCommentCommand(id, commentId, ifMatch.ExpectedVersion), ct);
        return result.Match<IResult>(
            () => TypedResults.NoContent(),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, traceId: httpContext.TraceIdentifier,
                includeStackTrace: _problemDetailsIncludeStackTrace)));
    }

    /// <summary>Adds a checklist item to the TaskItem aggregate through the root.</summary>
    private static async Task<IResult> AddChecklistItem(
        HttpContext httpContext,
        [FromServices] IRequestHandler<AddTaskItemChecklistItemCommand, Result<DefaultResponse<ChecklistItemDto>>> handler,
        Guid id,
        [FromBody] DefaultRequest<ChecklistItemDto> request,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new AddTaskItemChecklistItemCommand(id, request.Item), ct);
        return result.Match(
            response => response.Item is null
                ? Results.NotFound(id)
                : response.IsReplay
                    ? TypedResults.Ok(response)
                    : TypedResults.Created($"{httpContext.Request.Path}/{response.Item.Id}", response),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, traceId: httpContext.TraceIdentifier,
                includeStackTrace: _problemDetailsIncludeStackTrace)));
    }

    /// <summary>Updates a checklist item owned by the TaskItem aggregate.</summary>
    private static async Task<IResult> UpdateChecklistItem(
        HttpContext httpContext,
        [FromServices] IRequestHandler<UpdateTaskItemChecklistItemCommand, Result<DefaultResponse<ChecklistItemDto>>> handler,
        Guid id,
        Guid checklistItemId,
        IfMatch ifMatch,
        [FromBody] DefaultRequest<ChecklistItemDto> request,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(
            new UpdateTaskItemChecklistItemCommand(id, checklistItemId, request.Item, ifMatch.ExpectedVersion), ct);
        return result.Match(
            response => response.Item is null ? Results.NotFound(checklistItemId) : TypedResults.Ok(response),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, traceId: httpContext.TraceIdentifier,
                includeStackTrace: _problemDetailsIncludeStackTrace)));
    }

    /// <summary>Removes a checklist item from the TaskItem aggregate through the root.</summary>
    private static async Task<IResult> RemoveChecklistItem(
        HttpContext httpContext,
        [FromServices] IRequestHandler<RemoveTaskItemChecklistItemCommand, Result> handler,
        Guid id,
        Guid checklistItemId,
        IfMatch ifMatch,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(
            new RemoveTaskItemChecklistItemCommand(id, checklistItemId, ifMatch.ExpectedVersion), ct);
        return result.Match<IResult>(
            () => TypedResults.NoContent(),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, traceId: httpContext.TraceIdentifier,
                includeStackTrace: _problemDetailsIncludeStackTrace)));
    }

    /// <summary>Associates an existing Tag with the TaskItem aggregate through the root.</summary>
    private static async Task<IResult> AssociateTag(
        HttpContext httpContext,
        [FromServices] IRequestHandler<AssociateTaskItemTagCommand, Result<DefaultResponse<TaskItemTagDto>>> handler,
        Guid id,
        Guid tagId,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new AssociateTaskItemTagCommand(id, tagId), ct);
        return result.Match(
            response => response.Item is null
                ? Results.NotFound(id)
                : response.IsReplay
                    ? TypedResults.Ok(response)
                    : TypedResults.Created($"{httpContext.Request.Path}", response),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, traceId: httpContext.TraceIdentifier,
                includeStackTrace: _problemDetailsIncludeStackTrace)));
    }

    /// <summary>Removes a Tag association from the TaskItem aggregate through the root.</summary>
    private static async Task<IResult> RemoveTag(
        HttpContext httpContext,
        [FromServices] IRequestHandler<RemoveTaskItemTagCommand, Result> handler,
        Guid id,
        Guid tagId,
        IfMatch ifMatch,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new RemoveTaskItemTagCommand(id, tagId, ifMatch.ExpectedVersion), ct);
        return result.Match<IResult>(
            () => TypedResults.NoContent(),
            errors => TypedResults.Problem(ProblemDetailsHelper.BuildProblemDetailsResponseMultiple(
                errors: errors, traceId: httpContext.TraceIdentifier,
                includeStackTrace: _problemDetailsIncludeStackTrace)));
    }
}
