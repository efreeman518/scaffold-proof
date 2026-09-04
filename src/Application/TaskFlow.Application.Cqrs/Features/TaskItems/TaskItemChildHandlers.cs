using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Aggregates;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Cqrs.Shared;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Cqrs.Features.TaskItems;

// Handlers for the TaskItem aggregate's internal children. Every one loads the aggregate root through
// the shared TaskItemChildLoader - root only, no child collections - and mutates through the root's own
// domain methods, then saves in one transaction. Children are never created, updated, or deleted
// through a child repository, which would bypass the aggregate's invariants (GR-15).
//
// Concurrency: child writes carry the ROOT version as their If-Match currency (D-031), so the guard
// runs against the root immediately after the load and before any mutation.

/// <summary>Adds a comment to a TaskItem through the aggregate root.</summary>
internal sealed class AddTaskItemCommentHandler(
    ILogger<AddTaskItemCommentHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<AddTaskItemCommentCommand, Result<DefaultResponse<CommentDto>>>
{
    /// <summary>Handles add comment requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<CommentDto>>> HandleAsync(AddTaskItemCommentCommand command, CancellationToken ct = default)
    {
        var idCheck = UuidV7.ValidateCallerId(command.Comment.Id);
        if (idCheck.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(idCheck.ErrorMessage!);

        var (entity, error) = await TaskItemChildLoader.LoadRootAsync(
            repoTrxn, tenantBoundaryValidator, logger, requestContext.TenantId, requestContext.Roles,
            command.TaskItemId, "TaskItem:AddComment", ct);
        if (error is not null) return Result<DefaultResponse<CommentDto>>.Failure(error);
        if (entity is null) return HandlerHelpers.NotFoundResponse<CommentDto>();

        if (command.Comment.Id is Guid callerId && callerId != Guid.Empty)
        {
            var existing = await TaskItemChildLoader.LoadCommentAsync(repoTrxn, command.TaskItemId, callerId, ct);
            if (existing is not null)
            {
                var existingDto = existing.ToDto();
                if (!IdempotentCreateGuard.IsEquivalent(existingDto, command.Comment))
                    throw new IdempotentCreateConflictException(nameof(Comment), callerId);

                return Result<DefaultResponse<CommentDto>>.Success(
                    new DefaultResponse<CommentDto> { Item = existingDto, IsReplay = true });
            }
        }

        var addResult = entity.AddComment(command.Comment.Body, DomainId.FromNullable<CommentId>(command.Comment.Id));
        if (addResult.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(addResult.ErrorMessage!);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error adding Comment to TaskItem {Id}", ct, command.TaskItemId);
        if (save.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(save.ErrorMessage!);

        return HandlerHelpers.Success(addResult.Value!.ToDto());
    }
}

/// <summary>Updates a comment owned by a TaskItem through the aggregate root.</summary>
internal sealed class UpdateTaskItemCommentHandler(
    ILogger<UpdateTaskItemCommentHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<UpdateTaskItemCommentCommand, Result<DefaultResponse<CommentDto>>>
{
    /// <summary>Handles update comment requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<CommentDto>>> HandleAsync(UpdateTaskItemCommentCommand command, CancellationToken ct = default)
    {
        var (entity, error) = await TaskItemChildLoader.LoadRootAsync(
            repoTrxn, tenantBoundaryValidator, logger, requestContext.TenantId, requestContext.Roles,
            command.TaskItemId, "TaskItem:UpdateComment", ct);
        if (error is not null) return Result<DefaultResponse<CommentDto>>.Failure(error);
        if (entity is null) return HandlerHelpers.NotFoundResponse<CommentDto>();

        ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

        var comment = await TaskItemChildLoader.LoadCommentAsync(repoTrxn, command.TaskItemId, command.CommentId, ct);
        if (comment is null) return HandlerHelpers.NotFoundResponse<CommentDto>();

        var updateResult = comment.Update(command.Comment.Body);
        if (updateResult.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(updateResult.ErrorMessage!);
        entity.MarkChildMutated();

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error updating Comment {CommentId} on TaskItem {Id}", ct, command.CommentId, command.TaskItemId);
        if (save.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(save.ErrorMessage!);

        return HandlerHelpers.Success(comment.ToDto());
    }
}

/// <summary>Removes a comment from a TaskItem through the aggregate root.</summary>
internal sealed class RemoveTaskItemCommentHandler(
    ILogger<RemoveTaskItemCommentHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<RemoveTaskItemCommentCommand, Result>
{
    /// <summary>Handles remove comment requests and returns the application result.</summary>
    public async Task<Result> HandleAsync(RemoveTaskItemCommentCommand command, CancellationToken ct = default)
    {
        var (entity, error) = await TaskItemChildLoader.LoadRootAsync(
            repoTrxn, tenantBoundaryValidator, logger, requestContext.TenantId, requestContext.Roles,
            command.TaskItemId, "TaskItem:RemoveComment", ct);
        if (error is not null) return Result.Failure(error);
        if (entity is null) return Result.Success(); // Idempotent: parent gone means child gone.

        ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

        var comment = await TaskItemChildLoader.LoadCommentAsync(repoTrxn, command.TaskItemId, command.CommentId, ct);
        if (comment is not null)
        {
            // The root's child collections are not loaded, so the row is deleted explicitly rather
            // than by severing a navigation that would orphan it.
            entity.RemoveComment(comment);
            repoTrxn.DeleteChild(comment);
        }

        return await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error removing Comment {CommentId} from TaskItem {Id}", ct, command.CommentId, command.TaskItemId);
    }
}

/// <summary>Adds a checklist item to a TaskItem through the aggregate root.</summary>
internal sealed class AddTaskItemChecklistItemHandler(
    ILogger<AddTaskItemChecklistItemHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<AddTaskItemChecklistItemCommand, Result<DefaultResponse<ChecklistItemDto>>>
{
    /// <summary>Handles add checklist item requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<ChecklistItemDto>>> HandleAsync(AddTaskItemChecklistItemCommand command, CancellationToken ct = default)
    {
        var idCheck = UuidV7.ValidateCallerId(command.ChecklistItem.Id);
        if (idCheck.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(idCheck.ErrorMessage!);

        var (entity, error) = await TaskItemChildLoader.LoadRootAsync(
            repoTrxn, tenantBoundaryValidator, logger, requestContext.TenantId, requestContext.Roles,
            command.TaskItemId, "TaskItem:AddChecklistItem", ct);
        if (error is not null) return Result<DefaultResponse<ChecklistItemDto>>.Failure(error);
        if (entity is null) return HandlerHelpers.NotFoundResponse<ChecklistItemDto>();

        if (command.ChecklistItem.Id is Guid callerId && callerId != Guid.Empty)
        {
            var existing = await TaskItemChildLoader.LoadChecklistItemAsync(repoTrxn, command.TaskItemId, callerId, ct);
            if (existing is not null)
            {
                var existingDto = existing.ToDto();
                if (!IdempotentCreateGuard.IsEquivalent(existingDto, command.ChecklistItem))
                    throw new IdempotentCreateConflictException(nameof(ChecklistItem), callerId);

                return Result<DefaultResponse<ChecklistItemDto>>.Success(
                    new DefaultResponse<ChecklistItemDto> { Item = existingDto, IsReplay = true });
            }
        }

        var addResult = entity.AddChecklistItem(
            command.ChecklistItem.Title, command.ChecklistItem.SortOrder,
            DomainId.FromNullable<ChecklistItemId>(command.ChecklistItem.Id));
        if (addResult.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(addResult.ErrorMessage!);

        // AddChecklistItem/Create does not take IsCompleted; apply it on the new child so a
        // pre-checked item is not silently dropped.
        if (command.ChecklistItem.IsCompleted) addResult.Value!.Update(isCompleted: true);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error adding ChecklistItem to TaskItem {Id}", ct, command.TaskItemId);
        if (save.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(save.ErrorMessage!);

        return HandlerHelpers.Success(addResult.Value!.ToDto());
    }
}

/// <summary>Updates a checklist item owned by a TaskItem through the aggregate root.</summary>
internal sealed class UpdateTaskItemChecklistItemHandler(
    ILogger<UpdateTaskItemChecklistItemHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<UpdateTaskItemChecklistItemCommand, Result<DefaultResponse<ChecklistItemDto>>>
{
    /// <summary>Handles update checklist item requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<ChecklistItemDto>>> HandleAsync(UpdateTaskItemChecklistItemCommand command, CancellationToken ct = default)
    {
        var (entity, error) = await TaskItemChildLoader.LoadRootAsync(
            repoTrxn, tenantBoundaryValidator, logger, requestContext.TenantId, requestContext.Roles,
            command.TaskItemId, "TaskItem:UpdateChecklistItem", ct);
        if (error is not null) return Result<DefaultResponse<ChecklistItemDto>>.Failure(error);
        if (entity is null) return HandlerHelpers.NotFoundResponse<ChecklistItemDto>();

        ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

        var item = await TaskItemChildLoader.LoadChecklistItemAsync(repoTrxn, command.TaskItemId, command.ChecklistItemId, ct);
        if (item is null) return HandlerHelpers.NotFoundResponse<ChecklistItemDto>();

        var updateResult = item.Update(command.ChecklistItem.Title, command.ChecklistItem.IsCompleted, command.ChecklistItem.SortOrder);
        if (updateResult.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(updateResult.ErrorMessage!);
        entity.MarkChildMutated();

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error updating ChecklistItem {ChecklistItemId} on TaskItem {Id}", ct, command.ChecklistItemId, command.TaskItemId);
        if (save.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(save.ErrorMessage!);

        return HandlerHelpers.Success(item.ToDto());
    }
}

/// <summary>Removes a checklist item from a TaskItem through the aggregate root.</summary>
internal sealed class RemoveTaskItemChecklistItemHandler(
    ILogger<RemoveTaskItemChecklistItemHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<RemoveTaskItemChecklistItemCommand, Result>
{
    /// <summary>Handles remove checklist item requests and returns the application result.</summary>
    public async Task<Result> HandleAsync(RemoveTaskItemChecklistItemCommand command, CancellationToken ct = default)
    {
        var (entity, error) = await TaskItemChildLoader.LoadRootAsync(
            repoTrxn, tenantBoundaryValidator, logger, requestContext.TenantId, requestContext.Roles,
            command.TaskItemId, "TaskItem:RemoveChecklistItem", ct);
        if (error is not null) return Result.Failure(error);
        if (entity is null) return Result.Success(); // Idempotent.

        ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

        var item = await TaskItemChildLoader.LoadChecklistItemAsync(repoTrxn, command.TaskItemId, command.ChecklistItemId, ct);
        if (item is not null)
        {
            entity.RemoveChecklistItem(item);
            repoTrxn.DeleteChild(item);
        }

        return await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error removing ChecklistItem {ChecklistItemId} from TaskItem {Id}", ct, command.ChecklistItemId, command.TaskItemId);
    }
}

/// <summary>Associates an existing Tag with a TaskItem through the aggregate root.</summary>
internal sealed class AssociateTaskItemTagHandler(
    ILogger<AssociateTaskItemTagHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<AssociateTaskItemTagCommand, Result<DefaultResponse<TaskItemTagDto>>>
{
    /// <summary>Handles associate tag requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<TaskItemTagDto>>> HandleAsync(AssociateTaskItemTagCommand command, CancellationToken ct = default)
    {
        var (entity, error) = await TaskItemChildLoader.LoadRootAsync(
            repoTrxn, tenantBoundaryValidator, logger, requestContext.TenantId, requestContext.Roles,
            command.TaskItemId, "TaskItem:AssociateTag", ct);
        if (error is not null) return Result<DefaultResponse<TaskItemTagDto>>.Failure(error);
        if (entity is null) return HandlerHelpers.NotFoundResponse<TaskItemTagDto>();

        // Association is idempotent by tag id: an existing row is returned rather than duplicated.
        var existing = await TaskItemChildLoader.LoadTaskItemTagAsync(repoTrxn, command.TaskItemId, command.TagId, ct);
        if (existing is not null)
            return Result<DefaultResponse<TaskItemTagDto>>.Success(
                new DefaultResponse<TaskItemTagDto> { Item = existing.ToDto(), IsReplay = true });

        var associateResult = entity.AssociateTag(DomainId.From<TagId>(command.TagId));
        if (associateResult.IsFailure) return Result<DefaultResponse<TaskItemTagDto>>.Failure(associateResult.ErrorMessage!);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error associating Tag {TagId} with TaskItem {Id}", ct, command.TagId, command.TaskItemId);
        if (save.IsFailure) return Result<DefaultResponse<TaskItemTagDto>>.Failure(save.ErrorMessage!);

        return HandlerHelpers.Success(associateResult.Value!.ToDto());
    }
}

/// <summary>Removes a Tag association from a TaskItem through the aggregate root.</summary>
internal sealed class RemoveTaskItemTagHandler(
    ILogger<RemoveTaskItemTagHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<RemoveTaskItemTagCommand, Result>
{
    /// <summary>Handles remove tag requests and returns the application result.</summary>
    public async Task<Result> HandleAsync(RemoveTaskItemTagCommand command, CancellationToken ct = default)
    {
        var (entity, error) = await TaskItemChildLoader.LoadRootAsync(
            repoTrxn, tenantBoundaryValidator, logger, requestContext.TenantId, requestContext.Roles,
            command.TaskItemId, "TaskItem:RemoveTag", ct);
        if (error is not null) return Result.Failure(error);
        if (entity is null) return Result.Success(); // Idempotent.

        ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

        var association = await TaskItemChildLoader.LoadTaskItemTagAsync(repoTrxn, command.TaskItemId, command.TagId, ct);
        if (association is not null)
        {
            entity.RemoveTag(association);
            repoTrxn.DeleteChild(association);
        }

        return await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error removing Tag {TagId} from TaskItem {Id}", ct, command.TagId, command.TaskItemId);
    }
}
