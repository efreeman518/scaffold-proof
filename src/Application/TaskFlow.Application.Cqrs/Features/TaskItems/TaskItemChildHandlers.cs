using EF.Tenancy;
using EF.Domain.Contracts;
using EF.Data.Contracts;
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

        // An add carries no If-Match, so the caller's intent wins a race with another write to the aggregate
        // (D-031 bumps the root on every child write) or with a same-key add: a lost save re-reads and decides
        // again, and a race lost on every attempt is 409 (D-073).
        return await ConcurrencyRetry.RunAsync(repoTrxn, nameof(Comment), command.TaskItemId, async attemptCt =>
        {
            var (entity, error) = await TaskItemChildLoader.LoadRootAsync(
                repoTrxn, tenantBoundaryValidator, logger, requestContext.TenantId, requestContext.Roles,
                command.TaskItemId, "TaskItem:AddComment", attemptCt);
            if (error is not null) return Result<DefaultResponse<CommentDto>>.Failure(error);
            if (entity is null) return HandlerHelpers.NotFoundResponse<CommentDto>();

            if (command.Comment.Id is Guid callerId && callerId != Guid.Empty)
            {
                var existing = await TaskItemChildLoader.LoadCommentAsync(repoTrxn, command.TaskItemId, callerId, attemptCt);
                if (existing is not null)
                {
                    var existingDto = existing.ToDto();
                    if (!IdempotentCreateGuard.IsEquivalent(existingDto, command.Comment))
                        throw new ConflictException(nameof(Comment), callerId.ToString());

                    return Result<DefaultResponse<CommentDto>>.Success(
                        new DefaultResponse<CommentDto> { Item = existingDto, IsReplay = true, AggregateVersion = entity.Version });
                }
            }

            var addResult = entity.AddComment(command.Comment.Body, DomainId.FromNullable<CommentId>(command.Comment.Id));
            if (addResult.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(addResult.ErrorMessage!);

            var save = await CqrsHandlerSupport.TrySaveAddAsync(
                repoTrxn,
                command.Comment.Id is Guid id && id != Guid.Empty
                    ? async t => await TaskItemChildLoader.LoadCommentAsync(repoTrxn, command.TaskItemId, id, t) is not null
                    : null,
                logger, "Error adding Comment to TaskItem {Id}", attemptCt, command.TaskItemId);
            if (save.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(save.ErrorMessage!);

            return HandlerHelpers.SuccessForChild(addResult.Value!.ToDto(), entity.Version);
        }, ct);
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
        // If-Match: * re-reads the root and applies again when it loses a race (D-073); a concrete root
        // version keeps its 412.
        return await ConcurrencyRetry.RunAsync(repoTrxn, command.ExpectedVersion, nameof(TaskItem), command.TaskItemId, async attemptCt =>
        {
            var (entity, error) = await TaskItemChildLoader.LoadRootAsync(
                repoTrxn, tenantBoundaryValidator, logger, requestContext.TenantId, requestContext.Roles,
                command.TaskItemId, "TaskItem:UpdateComment", attemptCt);
            if (error is not null) return Result<DefaultResponse<CommentDto>>.Failure(error);
            if (entity is null) return HandlerHelpers.NotFoundResponse<CommentDto>();

            ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

            var comment = await TaskItemChildLoader.LoadCommentAsync(repoTrxn, command.TaskItemId, command.CommentId, attemptCt);
            if (comment is null) return HandlerHelpers.NotFoundResponse<CommentDto>();

            var updateResult = comment.Update(command.Comment.Body);
            if (updateResult.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(updateResult.ErrorMessage!);
            entity.MarkChildMutated();

            var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error updating Comment {CommentId} on TaskItem {Id}", attemptCt, command.CommentId, command.TaskItemId);
            if (save.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(save.ErrorMessage!);

            return HandlerHelpers.SuccessForChild(comment.ToDto(), entity.Version);
        }, ct);
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
        // If-Match: * re-reads the root and applies again when it loses a race (D-073); a concrete root
        // version keeps its 412.
        return await ConcurrencyRetry.RunAsync(repoTrxn, command.ExpectedVersion, nameof(TaskItem), command.TaskItemId, async attemptCt =>
        {
            var (entity, error) = await TaskItemChildLoader.LoadRootAsync(
                repoTrxn, tenantBoundaryValidator, logger, requestContext.TenantId, requestContext.Roles,
                command.TaskItemId, "TaskItem:RemoveComment", attemptCt);
            if (error is not null) return Result.Failure(error);
            if (entity is null) return Result.Success(); // Idempotent: parent gone means child gone.

            ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

            var comment = await TaskItemChildLoader.LoadCommentAsync(repoTrxn, command.TaskItemId, command.CommentId, attemptCt);
            if (comment is not null)
            {
                // The root's child collections are not loaded, so the row is deleted explicitly rather
                // than by severing a navigation that would orphan it.
                entity.RemoveComment(comment);
                repoTrxn.DeleteChild(comment);
            }

            return await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error removing Comment {CommentId} from TaskItem {Id}", attemptCt, command.CommentId, command.TaskItemId);
        }, ct);
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

        // An add carries no If-Match, so the caller's intent wins a race with another write to the aggregate
        // (D-031 bumps the root on every child write) or with a same-key add: a lost save re-reads and decides
        // again, and a race lost on every attempt is 409 (D-073).
        return await ConcurrencyRetry.RunAsync(repoTrxn, nameof(ChecklistItem), command.TaskItemId, async attemptCt =>
        {
            var (entity, error) = await TaskItemChildLoader.LoadRootAsync(
                repoTrxn, tenantBoundaryValidator, logger, requestContext.TenantId, requestContext.Roles,
                command.TaskItemId, "TaskItem:AddChecklistItem", attemptCt);
            if (error is not null) return Result<DefaultResponse<ChecklistItemDto>>.Failure(error);
            if (entity is null) return HandlerHelpers.NotFoundResponse<ChecklistItemDto>();

            if (command.ChecklistItem.Id is Guid callerId && callerId != Guid.Empty)
            {
                var existing = await TaskItemChildLoader.LoadChecklistItemAsync(repoTrxn, command.TaskItemId, callerId, attemptCt);
                if (existing is not null)
                {
                    var existingDto = existing.ToDto();
                    if (!IdempotentCreateGuard.IsEquivalent(existingDto, command.ChecklistItem))
                        throw new ConflictException(nameof(ChecklistItem), callerId.ToString());

                    return Result<DefaultResponse<ChecklistItemDto>>.Success(
                        new DefaultResponse<ChecklistItemDto> { Item = existingDto, IsReplay = true, AggregateVersion = entity.Version });
                }
            }

            var addResult = entity.AddChecklistItem(
                command.ChecklistItem.Title, command.ChecklistItem.SortOrder,
                DomainId.FromNullable<ChecklistItemId>(command.ChecklistItem.Id));
            if (addResult.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(addResult.ErrorMessage!);

            // AddChecklistItem/Create does not take IsCompleted; apply it on the new child so a
            // pre-checked item is not silently dropped.
            if (command.ChecklistItem.IsCompleted) addResult.Value!.Update(isCompleted: true);

            var save = await CqrsHandlerSupport.TrySaveAddAsync(
                repoTrxn,
                command.ChecklistItem.Id is Guid id && id != Guid.Empty
                    ? async t => await TaskItemChildLoader.LoadChecklistItemAsync(repoTrxn, command.TaskItemId, id, t) is not null
                    : null,
                logger, "Error adding ChecklistItem to TaskItem {Id}", attemptCt, command.TaskItemId);
            if (save.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(save.ErrorMessage!);

            return HandlerHelpers.SuccessForChild(addResult.Value!.ToDto(), entity.Version);
        }, ct);
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
        // If-Match: * re-reads the root and applies again when it loses a race (D-073); a concrete root
        // version keeps its 412.
        return await ConcurrencyRetry.RunAsync(repoTrxn, command.ExpectedVersion, nameof(TaskItem), command.TaskItemId, async attemptCt =>
        {
            var (entity, error) = await TaskItemChildLoader.LoadRootAsync(
                repoTrxn, tenantBoundaryValidator, logger, requestContext.TenantId, requestContext.Roles,
                command.TaskItemId, "TaskItem:UpdateChecklistItem", attemptCt);
            if (error is not null) return Result<DefaultResponse<ChecklistItemDto>>.Failure(error);
            if (entity is null) return HandlerHelpers.NotFoundResponse<ChecklistItemDto>();

            ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

            var item = await TaskItemChildLoader.LoadChecklistItemAsync(repoTrxn, command.TaskItemId, command.ChecklistItemId, attemptCt);
            if (item is null) return HandlerHelpers.NotFoundResponse<ChecklistItemDto>();

            var updateResult = item.Update(command.ChecklistItem.Title, command.ChecklistItem.IsCompleted, command.ChecklistItem.SortOrder);
            if (updateResult.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(updateResult.ErrorMessage!);
            entity.MarkChildMutated();

            var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error updating ChecklistItem {ChecklistItemId} on TaskItem {Id}", attemptCt, command.ChecklistItemId, command.TaskItemId);
            if (save.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(save.ErrorMessage!);

            return HandlerHelpers.SuccessForChild(item.ToDto(), entity.Version);
        }, ct);
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
        // If-Match: * re-reads the root and applies again when it loses a race (D-073); a concrete root
        // version keeps its 412.
        return await ConcurrencyRetry.RunAsync(repoTrxn, command.ExpectedVersion, nameof(TaskItem), command.TaskItemId, async attemptCt =>
        {
            var (entity, error) = await TaskItemChildLoader.LoadRootAsync(
                repoTrxn, tenantBoundaryValidator, logger, requestContext.TenantId, requestContext.Roles,
                command.TaskItemId, "TaskItem:RemoveChecklistItem", attemptCt);
            if (error is not null) return Result.Failure(error);
            if (entity is null) return Result.Success(); // Idempotent.

            ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

            var item = await TaskItemChildLoader.LoadChecklistItemAsync(repoTrxn, command.TaskItemId, command.ChecklistItemId, attemptCt);
            if (item is not null)
            {
                entity.RemoveChecklistItem(item);
                repoTrxn.DeleteChild(item);
            }

            return await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error removing ChecklistItem {ChecklistItemId} from TaskItem {Id}", attemptCt, command.ChecklistItemId, command.TaskItemId);
        }, ct);
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
        // An add carries no If-Match, so the caller's intent wins a race with another write to the aggregate
        // (D-031 bumps the root on every child write) or with a same-key add: a lost save re-reads and decides
        // again, and a race lost on every attempt is 409 (D-073).
        return await ConcurrencyRetry.RunAsync(repoTrxn, nameof(TaskItemTag), command.TaskItemId, async attemptCt =>
        {
            var (entity, error) = await TaskItemChildLoader.LoadRootAsync(
                repoTrxn, tenantBoundaryValidator, logger, requestContext.TenantId, requestContext.Roles,
                command.TaskItemId, "TaskItem:AssociateTag", attemptCt);
            if (error is not null) return Result<DefaultResponse<TaskItemTagDto>>.Failure(error);
            if (entity is null) return HandlerHelpers.NotFoundResponse<TaskItemTagDto>();

            // Association is idempotent by tag id: an existing row is returned rather than duplicated.
            var existing = await TaskItemChildLoader.LoadTaskItemTagAsync(repoTrxn, command.TaskItemId, command.TagId, attemptCt);
            if (existing is not null)
                return Result<DefaultResponse<TaskItemTagDto>>.Success(
                    new DefaultResponse<TaskItemTagDto> { Item = existing.ToDto(), IsReplay = true, AggregateVersion = entity.Version });

            var associateResult = entity.AssociateTag(TagId.From(command.TagId));
            if (associateResult.IsFailure) return Result<DefaultResponse<TaskItemTagDto>>.Failure(associateResult.ErrorMessage!);

            var save = await CqrsHandlerSupport.TrySaveAddAsync(
                repoTrxn,
                async t => await TaskItemChildLoader.LoadTaskItemTagAsync(repoTrxn, command.TaskItemId, command.TagId, t) is not null,
                logger, "Error associating Tag {TagId} with TaskItem {Id}", attemptCt, command.TagId, command.TaskItemId);
            if (save.IsFailure) return Result<DefaultResponse<TaskItemTagDto>>.Failure(save.ErrorMessage!);

            return HandlerHelpers.SuccessForChild(associateResult.Value!.ToDto(), entity.Version);
        }, ct);
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
        // If-Match: * re-reads the root and applies again when it loses a race (D-073); a concrete root
        // version keeps its 412.
        return await ConcurrencyRetry.RunAsync(repoTrxn, command.ExpectedVersion, nameof(TaskItem), command.TaskItemId, async attemptCt =>
        {
            var (entity, error) = await TaskItemChildLoader.LoadRootAsync(
                repoTrxn, tenantBoundaryValidator, logger, requestContext.TenantId, requestContext.Roles,
                command.TaskItemId, "TaskItem:RemoveTag", attemptCt);
            if (error is not null) return Result.Failure(error);
            if (entity is null) return Result.Success(); // Idempotent.

            ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

            var association = await TaskItemChildLoader.LoadTaskItemTagAsync(repoTrxn, command.TaskItemId, command.TagId, attemptCt);
            if (association is not null)
            {
                entity.RemoveTag(association);
                repoTrxn.DeleteChild(association);
            }

            return await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error removing Tag {TagId} from TaskItem {Id}", attemptCt, command.TagId, command.TaskItemId);
        }, ct);
    }
}
