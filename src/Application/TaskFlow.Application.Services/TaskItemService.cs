using EF.Common.Contracts;
using EF.Data.Contracts;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Application.Services.Rules;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Model.ValueObjects;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Application.Services;

/// <summary>
/// Service-style TaskItem application boundary. It enforces tenant scope, delegates rules to the
/// aggregate, and persists through transaction repositories. Integration events are raised by the
/// aggregate and staged as outbox rows by the persistence interceptor (D-026); nothing is published here.
/// </summary>
internal class TaskItemService(
    ILogger<TaskItemService> logger,
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryTrxn repoTrxn,
    ITaskItemRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator,
    IEntityCacheProvider cache) : ITaskItemService
{
    private Guid? RequestTenantId => requestContext.TenantId;
    private IReadOnlyCollection<string> RequestRoles => requestContext.Roles;
    private bool IsGlobalAdmin => RequestRoles.Contains(AppConstants.ROLE_GLOBAL_ADMIN);

    #region Helpers

    /// <summary>Builds response from current configuration and inputs.</summary>
    private static DefaultResponse<TaskItemDto> BuildResponse(TaskItemDto dto) =>
        new() { Item = dto, TenantInfo = null };

    #endregion

    /// <summary>Searches search and returns filtered results for callers.</summary>
    public async Task<PagedResponse<TaskItemDto>> SearchAsync(
        SearchRequest<TaskItemSearchFilter> request, CancellationToken ct = default)
    {
        if (!IsGlobalAdmin)
        {
            request.Filter ??= new();
            if (request.Filter.TenantId is Guid supplied && supplied != RequestTenantId)
            {
                logger.LogTenantFilterManipulation("TaskItemSearch", RequestTenantId, supplied);
            }
            request.Filter.TenantId = RequestTenantId;
        }
        try
        {
            return await repoQuery.SearchTaskItemsAsync(request, ct);
        }
        catch (OperationCanceledException)
        {
            logger.LogDebug("TaskItem search cancelled by client.");
            return new PagedResponse<TaskItemDto>();
        }
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Result<DefaultResponse<TaskItemDto>>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await repoQuery.GetTaskItemAsync(DomainId.From<TaskItemId>(id), ct);
        if (entity == null) return Result<DefaultResponse<TaskItemDto>>.None();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, entity.TenantId.Value,
            "TaskItem:Get", nameof(TaskItem), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(boundary.ErrorMessage!);

        return Result<DefaultResponse<TaskItemDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>
    /// Creates the aggregate under the caller tenant, saves it first, then publishes the created event.
    /// Event publishing is best-effort because the database save is the source of truth.
    /// </summary>
    public async Task<Result<DefaultResponse<TaskItemDto>>> CreateAsync(
        DefaultRequest<TaskItemDto> request, CancellationToken ct = default)
    {
        var dto = request.Item;
        dto.TenantId = RequestTenantId ?? Guid.Empty;

        var validation = TaskItemStructureValidator.ValidateCreate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(validation.Errors);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, dto.TenantId,
            "TaskItem:Create", nameof(TaskItem));
        if (boundary.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(boundary.ErrorMessage!);

        var entityResult = dto.ToEntity(dto.TenantId)
            .Bind(e => repoTrxn.UpdateFromDto(e, dto));
        if (entityResult.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(entityResult.ErrorMessage!);

        var entity = entityResult.Value!;
        repoTrxn.Create(ref entity);

        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error creating TaskItem");
            return Result<DefaultResponse<TaskItemDto>>.Failure(ex.GetBaseException().Message);
        }

        var resultDto = entity.ToDto();

        return Result<DefaultResponse<TaskItemDto>>.Success(BuildResponse(resultDto));
    }

    /// <summary>
    /// Updates the aggregate and child collections from one DTO payload. Status transitions run
    /// through the aggregate before the updater syncs children so invalid transitions fail before save.
    /// </summary>
    public async Task<Result<DefaultResponse<TaskItemDto>>> UpdateAsync(
        DefaultRequest<TaskItemDto> request, CancellationToken ct = default)
    {
        var dto = request.Item;
        dto.TenantId = RequestTenantId ?? Guid.Empty;

        var validation = TaskItemStructureValidator.ValidateUpdate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(validation.Errors);

        var entity = await repoTrxn.GetTaskItemAsync(DomainId.From<TaskItemId>(dto.Id!.Value), ct: ct);
        if (entity == null)
            return Result<DefaultResponse<TaskItemDto>>.Success(new DefaultResponse<TaskItemDto> { Item = null });

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, entity.TenantId.Value,
            "TaskItem:Update", nameof(TaskItem), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(boundary.ErrorMessage!);

        var tenantChangeCheck = tenantBoundaryValidator.PreventTenantChange(
            logger, entity.TenantId.Value, dto.TenantId, nameof(TaskItem), entity.Id.Value);
        if (tenantChangeCheck.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(tenantChangeCheck.ErrorMessage!);

        // Handle status transition if changed. The aggregate raises the status/completed events (D-026).
        if (dto.Status != entity.Status)
        {
            var transitionResult = entity.TransitionStatus(dto.Status);
            if (transitionResult.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(transitionResult.ErrorMessage!);
        }

        var updateResult = entity.Update(
            dto.Title, dto.Description, dto.Priority, dto.Features,
            dto.EstimatedEffort, dto.ActualEffort,
            DomainId.FromNullable<CategoryId>(dto.CategoryId),
            DomainId.FromNullable<TaskItemId>(dto.ParentTaskItemId));
        if (updateResult.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(updateResult.ErrorMessage!);

        // Update value objects
        entity.UpdateDateRange(dto.StartDate, dto.DueDate);

        if (dto.RecurrenceInterval.HasValue && !string.IsNullOrEmpty(dto.RecurrenceFrequency))
        {
            entity.UpdateRecurrencePattern(new RecurrencePattern
            {
                Interval = dto.RecurrenceInterval.Value,
                Frequency = dto.RecurrenceFrequency!,
                EndDate = dto.RecurrenceEndDate
            });
        }
        else
        {
            entity.UpdateRecurrencePattern(null);
        }

        // Sync child collections via Updater - removed items must be hard-deleted,
        // not just unlinked, so the UI's single-payload save can retire checklist
        // items / comments that were removed client-side.
        var syncResult = repoTrxn.UpdateFromDto(entity, dto, RelatedDeleteBehavior.RelationshipAndEntity);
        if (syncResult.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(syncResult.ErrorMessage!);

        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error updating TaskItem {Id}", dto.Id);
            return Result<DefaultResponse<TaskItemDto>>.Failure(ex.GetBaseException().Message);
        }

        var resultDto = entity.ToDto();

        return Result<DefaultResponse<TaskItemDto>>.Success(BuildResponse(resultDto));
    }

    /// <summary>
    /// Applies a sparse partial update to an existing TaskItem. Only the non-null fields on the patch
    /// are changed; everything else is left intact. Delegates the merge to the aggregate's own
    /// <see cref="TaskItem.Update"/> (which already ignores null arguments), so PATCH reuses the same
    /// domain invariants as PUT without forcing the caller to resend the whole aggregate.
    /// </summary>
    public async Task<Result<DefaultResponse<TaskItemDto>>> PatchAsync(
        Guid id, TaskItemPatchDto patch, CancellationToken ct = default)
    {
        var entity = await repoTrxn.GetTaskItemAsync(DomainId.From<TaskItemId>(id), ct: ct);
        if (entity == null)
            return Result<DefaultResponse<TaskItemDto>>.Success(new DefaultResponse<TaskItemDto> { Item = null });

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, entity.TenantId.Value,
            "TaskItem:Patch", nameof(TaskItem), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(boundary.ErrorMessage!);

        var updateResult = entity.Update(
            title: patch.Title,
            description: patch.Description,
            priority: patch.Priority,
            estimatedEffort: patch.EstimatedEffort,
            categoryId: DomainId.FromNullable<CategoryId>(patch.CategoryId),
            parentTaskItemId: DomainId.FromNullable<TaskItemId>(patch.ParentTaskItemId));
        if (updateResult.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(updateResult.ErrorMessage!);

        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error patching TaskItem {Id}", id);
            return Result<DefaultResponse<TaskItemDto>>.Failure(ex.GetBaseException().Message);
        }

        await cache.RemoveAsync($"TaskItem:{id}", ct);
        return Result<DefaultResponse<TaskItemDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await repoTrxn.GetTaskItemAsync(DomainId.From<TaskItemId>(id), ct: ct);
        if (entity == null) return Result.Success();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, entity.TenantId.Value,
            "TaskItem:Delete", nameof(TaskItem), entity.Id.Value);
        if (boundary.IsFailure) return Result.Failure(boundary.ErrorMessage!);

        repoTrxn.Delete(entity);

        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error deleting TaskItem {Id}", id);
            return Result.Failure(ex.GetBaseException().Message);
        }

        await cache.RemoveAsync($"TaskItem:{id}", ct);
        return Result.Success();
    }

    #region Nested children (mutated through the aggregate root - GR-15)

    /// <summary>Saves the tracked aggregate graph and maps failures to a Result.</summary>
    private async Task<Result> SaveAggregateAsync(string errorMessage, CancellationToken ct, params object?[] args)
    {
        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, ct);
            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "{ErrorMessage} {@Args}", errorMessage, args);
            return Result.Failure(ex.GetBaseException().Message);
        }
    }

    /// <summary>Loads the aggregate and enforces the caller tenant boundary before a child mutation.</summary>
    private async Task<(TaskItem? Entity, string? Error)> LoadForChildMutationAsync(Guid taskItemId, string operation, CancellationToken ct)
    {
        var entity = await repoTrxn.GetTaskItemAsync(DomainId.From<TaskItemId>(taskItemId), ct: ct);
        if (entity is null) return (null, null);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, entity.TenantId.Value, operation, nameof(TaskItem), entity.Id.Value);
        return boundary.IsFailure ? (null, boundary.ErrorMessage!) : (entity, null);
    }

    /// <summary>Adds a comment to a TaskItem through the aggregate root.</summary>
    public async Task<Result<DefaultResponse<CommentDto>>> AddCommentAsync(Guid taskItemId, CommentDto comment, CancellationToken ct = default)
    {
        var (entity, error) = await LoadForChildMutationAsync(taskItemId, "TaskItem:AddComment", ct);
        if (error is not null) return Result<DefaultResponse<CommentDto>>.Failure(error);
        if (entity is null) return Result<DefaultResponse<CommentDto>>.Success(new DefaultResponse<CommentDto> { Item = null });

        var addResult = entity.AddComment(comment.Body);
        if (addResult.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(addResult.ErrorMessage!);

        var save = await SaveAggregateAsync("Error adding Comment to TaskItem {Id}", ct, taskItemId);
        if (save.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(save.ErrorMessage!);

        return Result<DefaultResponse<CommentDto>>.Success(new DefaultResponse<CommentDto> { Item = addResult.Value!.ToDto() });
    }

    /// <summary>Updates a comment owned by a TaskItem through the aggregate root.</summary>
    public async Task<Result<DefaultResponse<CommentDto>>> UpdateCommentAsync(Guid taskItemId, Guid commentId, CommentDto comment, CancellationToken ct = default)
    {
        var (entity, error) = await LoadForChildMutationAsync(taskItemId, "TaskItem:UpdateComment", ct);
        if (error is not null) return Result<DefaultResponse<CommentDto>>.Failure(error);
        if (entity is null) return Result<DefaultResponse<CommentDto>>.Success(new DefaultResponse<CommentDto> { Item = null });

        var typedCommentId = DomainId.From<CommentId>(commentId);
        var target = entity.Comments.FirstOrDefault(c => c.Id == typedCommentId);
        if (target is null) return Result<DefaultResponse<CommentDto>>.Success(new DefaultResponse<CommentDto> { Item = null });

        var updateResult = target.Update(comment.Body);
        if (updateResult.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(updateResult.ErrorMessage!);

        var save = await SaveAggregateAsync("Error updating Comment {CommentId} on TaskItem {Id}", ct, commentId, taskItemId);
        if (save.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(save.ErrorMessage!);

        return Result<DefaultResponse<CommentDto>>.Success(new DefaultResponse<CommentDto> { Item = target.ToDto() });
    }

    /// <summary>Removes a comment from a TaskItem through the aggregate root.</summary>
    public async Task<Result> RemoveCommentAsync(Guid taskItemId, Guid commentId, CancellationToken ct = default)
    {
        var (entity, error) = await LoadForChildMutationAsync(taskItemId, "TaskItem:RemoveComment", ct);
        if (error is not null) return Result.Failure(error);
        if (entity is null) return Result.Success();

        entity.RemoveComment(DomainId.From<CommentId>(commentId));
        return await SaveAggregateAsync("Error removing Comment {CommentId} from TaskItem {Id}", ct, commentId, taskItemId);
    }

    /// <summary>Adds a checklist item to a TaskItem through the aggregate root.</summary>
    public async Task<Result<DefaultResponse<ChecklistItemDto>>> AddChecklistItemAsync(Guid taskItemId, ChecklistItemDto checklistItem, CancellationToken ct = default)
    {
        var (entity, error) = await LoadForChildMutationAsync(taskItemId, "TaskItem:AddChecklistItem", ct);
        if (error is not null) return Result<DefaultResponse<ChecklistItemDto>>.Failure(error);
        if (entity is null) return Result<DefaultResponse<ChecklistItemDto>>.Success(new DefaultResponse<ChecklistItemDto> { Item = null });

        var addResult = entity.AddChecklistItem(checklistItem.Title, checklistItem.SortOrder);
        if (addResult.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(addResult.ErrorMessage!);
        if (checklistItem.IsCompleted) addResult.Value!.Update(isCompleted: true);

        var save = await SaveAggregateAsync("Error adding ChecklistItem to TaskItem {Id}", ct, taskItemId);
        if (save.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(save.ErrorMessage!);

        return Result<DefaultResponse<ChecklistItemDto>>.Success(new DefaultResponse<ChecklistItemDto> { Item = addResult.Value!.ToDto() });
    }

    /// <summary>Updates a checklist item owned by a TaskItem through the aggregate root.</summary>
    public async Task<Result<DefaultResponse<ChecklistItemDto>>> UpdateChecklistItemAsync(Guid taskItemId, Guid checklistItemId, ChecklistItemDto checklistItem, CancellationToken ct = default)
    {
        var (entity, error) = await LoadForChildMutationAsync(taskItemId, "TaskItem:UpdateChecklistItem", ct);
        if (error is not null) return Result<DefaultResponse<ChecklistItemDto>>.Failure(error);
        if (entity is null) return Result<DefaultResponse<ChecklistItemDto>>.Success(new DefaultResponse<ChecklistItemDto> { Item = null });

        var typedChecklistItemId = DomainId.From<ChecklistItemId>(checklistItemId);
        var target = entity.ChecklistItems.FirstOrDefault(c => c.Id == typedChecklistItemId);
        if (target is null) return Result<DefaultResponse<ChecklistItemDto>>.Success(new DefaultResponse<ChecklistItemDto> { Item = null });

        var updateResult = target.Update(checklistItem.Title, checklistItem.IsCompleted, checklistItem.SortOrder);
        if (updateResult.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(updateResult.ErrorMessage!);

        var save = await SaveAggregateAsync("Error updating ChecklistItem {ChecklistItemId} on TaskItem {Id}", ct, checklistItemId, taskItemId);
        if (save.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(save.ErrorMessage!);

        return Result<DefaultResponse<ChecklistItemDto>>.Success(new DefaultResponse<ChecklistItemDto> { Item = target.ToDto() });
    }

    /// <summary>Removes a checklist item from a TaskItem through the aggregate root.</summary>
    public async Task<Result> RemoveChecklistItemAsync(Guid taskItemId, Guid checklistItemId, CancellationToken ct = default)
    {
        var (entity, error) = await LoadForChildMutationAsync(taskItemId, "TaskItem:RemoveChecklistItem", ct);
        if (error is not null) return Result.Failure(error);
        if (entity is null) return Result.Success();

        entity.RemoveChecklistItem(DomainId.From<ChecklistItemId>(checklistItemId));
        return await SaveAggregateAsync("Error removing ChecklistItem {ChecklistItemId} from TaskItem {Id}", ct, checklistItemId, taskItemId);
    }

    /// <summary>Associates an existing Tag with a TaskItem through the aggregate root.</summary>
    public async Task<Result<DefaultResponse<TaskItemTagDto>>> AssociateTagAsync(Guid taskItemId, Guid tagId, CancellationToken ct = default)
    {
        var (entity, error) = await LoadForChildMutationAsync(taskItemId, "TaskItem:AssociateTag", ct);
        if (error is not null) return Result<DefaultResponse<TaskItemTagDto>>.Failure(error);
        if (entity is null) return Result<DefaultResponse<TaskItemTagDto>>.Success(new DefaultResponse<TaskItemTagDto> { Item = null });

        var associateResult = entity.AssociateTag(DomainId.From<TagId>(tagId));
        if (associateResult.IsFailure) return Result<DefaultResponse<TaskItemTagDto>>.Failure(associateResult.ErrorMessage!);

        var save = await SaveAggregateAsync("Error associating Tag {TagId} with TaskItem {Id}", ct, tagId, taskItemId);
        if (save.IsFailure) return Result<DefaultResponse<TaskItemTagDto>>.Failure(save.ErrorMessage!);

        return Result<DefaultResponse<TaskItemTagDto>>.Success(new DefaultResponse<TaskItemTagDto> { Item = associateResult.Value!.ToDto() });
    }

    /// <summary>Removes a Tag association from a TaskItem through the aggregate root.</summary>
    public async Task<Result> RemoveTagAsync(Guid taskItemId, Guid tagId, CancellationToken ct = default)
    {
        var (entity, error) = await LoadForChildMutationAsync(taskItemId, "TaskItem:RemoveTag", ct);
        if (error is not null) return Result.Failure(error);
        if (entity is null) return Result.Success();

        entity.RemoveTag(DomainId.From<TagId>(tagId));
        return await SaveAggregateAsync("Error removing Tag {TagId} from TaskItem {Id}", ct, tagId, taskItemId);
    }

    #endregion
}
