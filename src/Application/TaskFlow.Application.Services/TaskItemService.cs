using EF.Cache;
using EF.Tenancy;
using EF.Domain.Contracts;
using EF.Common.Contracts;
using EF.Data.Contracts;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Aggregates;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
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
    ITypedCache cache) : ITaskItemService
{
    private Guid? RequestTenantId => requestContext.TenantId;
    private IReadOnlyCollection<string> RequestRoles => requestContext.Roles;

    #region Helpers

    /// <summary>Builds response from current configuration and inputs.</summary>
    private static DefaultResponse<TaskItemDto> BuildResponse(TaskItemDto dto) =>
        new() { Item = dto, TenantInfo = null };

    /// <summary>
    /// Evicts the tenant snapshots a task write invalidates (the summary counts). Called only after a
    /// successful commit: evicting first would let a concurrent read repopulate the entry from the pre-commit
    /// state and leave it wrong until it expires. Child mutations do not call this - adding a comment changes
    /// no count in any cached snapshot.
    /// </summary>
    private Task InvalidateTaskSnapshotsAsync(CancellationToken ct) =>
        cache.RemoveByTagAsync(CacheTags.Entity(RequestTenantId ?? Guid.Empty, CacheTags.TaskItem), ct);

    #endregion

    /// <summary>
    /// Keyset page of task items. An out-of-range page size throws InvalidRequestException and a bad cursor
    /// InvalidCursorException (both mapped to 400)
    /// rather than clamping or silently restarting at page one - a silent restart would hand the caller
    /// rows it already read and look like duplicated data.
    /// </summary>
    public async Task<CursorPage<TaskItemDto>> SearchAsync(
        TaskItemCursorSearchRequest request, CancellationToken ct = default)
    {
        if (!PageSizeLimits.IsValid(request.PageSize))
            throw new InvalidRequestException(
                string.Format(ErrorConstants.ERROR_PAGE_SIZE_RANGE, PageSizeLimits.Min, PageSizeLimits.Max));

        request.Filter = tenantBoundaryValidator.EnforceTenantFilter(request.Filter, RequestTenantId, RequestRoles, "TaskItemSearch");

        var tenantId = request.Filter?.TenantId ?? RequestTenantId ?? Guid.Empty;

        // The cursor is decoded and the next one minted by the repository, which owns the codec: a
        // faulted cursor arrives here as InvalidCursorException (ERROR_CURSOR_INVALID), mapped to 400. A
        // cancellation or request timeout propagates (499/504): an empty page with HasMore = false would
        // tell a pager it had seen every row.
        return await repoQuery.SearchTaskItemsAsync(request, tenantId, ct);
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Result<DefaultResponse<TaskItemDto>>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await repoQuery.GetTaskItemAsync(TaskItemId.From(id), ct);
        if (entity == null) return Result<DefaultResponse<TaskItemDto>>.None();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            RequestTenantId, RequestRoles, entity.TenantId.Value,
            "TaskItem:Get", nameof(TaskItem), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(boundary.ErrorMessage!);

        return Result<DefaultResponse<TaskItemDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>
    /// Creates the aggregate under the caller tenant, saves it first, then publishes the created event.
    /// Event publishing is best-effort because the database save is the source of truth.
    ///
    /// A caller-supplied UUIDv7 id makes the create idempotent (D-033): the row itself is the
    /// idempotency record, so a repeated request with an equivalent payload replays the stored entity
    /// and a divergent one is a conflict.
    /// </summary>
    public async Task<Result<DefaultResponse<TaskItemDto>>> CreateAsync(
        DefaultRequest<TaskItemDto> request, CancellationToken ct = default)
    {
        var dto = request.Item;
        dto.TenantId = RequestTenantId ?? Guid.Empty;

        var validation = TaskItemStructureValidator.ValidateCreate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(validation.Errors);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            RequestTenantId, RequestRoles, dto.TenantId,
            "TaskItem:Create", nameof(TaskItem));
        if (boundary.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(boundary.ErrorMessage!);

        if (dto.Id is Guid callerId && callerId != Guid.Empty)
        {
            var existing = await repoTrxn.GetTaskItemAsync(TaskItemId.From(callerId), inclChildren: false, ct);
            if (existing is not null)
            {
                return Result<DefaultResponse<TaskItemDto>>.Success(IdempotentCreateGuard.ReplayOrThrow(
                    existing.ToDto(), dto, IdempotentCreateGuard.IsEquivalent, nameof(TaskItem), callerId));
            }
        }

        var entityResult = dto.ToEntity(dto.TenantId)
            .Bind(e => repoTrxn.UpdateFromDto(e, dto));
        if (entityResult.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(entityResult.ErrorMessage!);

        var entity = entityResult.Value!;
        repoTrxn.Create(ref entity);

        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.TaskItemCreateFailed(ex);

            // D-033: a concurrent create with the same id passed the existence check too and won the insert.
            // Re-read on the query context (this one still tracks the failed insert): the winner makes this a
            // replay or a 409. Absent (or not yet replicated) means the save failed for another reason.
            if (dto.Id is Guid racedId && racedId != Guid.Empty
                && await repoQuery.GetTaskItemAsync(TaskItemId.From(racedId), ct) is { } raced)
            {
                return Result<DefaultResponse<TaskItemDto>>.Success(IdempotentCreateGuard.ReplayOrThrow(
                    raced.ToDto(), dto, IdempotentCreateGuard.IsEquivalent, nameof(TaskItem), racedId));
            }

            return Result<DefaultResponse<TaskItemDto>>.Failure(ErrorConstants.ERROR_SAVE_FAILED);
        }

        await InvalidateTaskSnapshotsAsync(ct);
        var resultDto = entity.ToDto();

        return Result<DefaultResponse<TaskItemDto>>.Success(BuildResponse(resultDto));
    }

    /// <summary>
    /// Updates the aggregate and child collections from one DTO payload. Status transitions run
    /// through the aggregate before the updater syncs children so invalid transitions fail before save.
    /// </summary>
    public async Task<Result<DefaultResponse<TaskItemDto>>> UpdateAsync(
        DefaultRequest<TaskItemDto> request, long? expectedVersion, CancellationToken ct = default)
    {
        var dto = request.Item;
        dto.TenantId = RequestTenantId ?? Guid.Empty;

        var validation = TaskItemStructureValidator.ValidateUpdate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(validation.Errors);

        // If-Match: * re-reads and applies again when it loses a race (D-073); a concrete version keeps its 412.
        var result = await ConcurrencyRetry.RunAsync(repoTrxn, expectedVersion, nameof(TaskItem), dto.Id!.Value,
            attemptCt => UpdateOnceAsync(dto, expectedVersion, attemptCt), ct);
        if (result.IsSuccess && result.Value!.Item is not null) await InvalidateTaskSnapshotsAsync(ct);
        return result;
    }

    /// <summary>One read, update and save of <see cref="UpdateAsync"/>; run again on a lost wildcard race.</summary>
    private async Task<Result<DefaultResponse<TaskItemDto>>> UpdateOnceAsync(TaskItemDto dto, long? expectedVersion, CancellationToken ct)
    {
        var entity = await repoTrxn.GetTaskItemAsync(TaskItemId.From(dto.Id!.Value), ct: ct);
        if (entity == null)
            return Result<DefaultResponse<TaskItemDto>>.Success(new DefaultResponse<TaskItemDto> { Item = null });

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            RequestTenantId, RequestRoles, entity.TenantId.Value,
            "TaskItem:Update", nameof(TaskItem), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(boundary.ErrorMessage!);

        // After load, before any mutation: a stale caller must not run the status state machine.
        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

        var tenantChangeCheck = tenantBoundaryValidator.PreventTenantChange(
            entity.TenantId.Value, dto.TenantId, nameof(TaskItem), entity.Id.Value);
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
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.TaskItemUpdateFailed(ex, dto.Id);
            return Result<DefaultResponse<TaskItemDto>>.Failure(ErrorConstants.ERROR_SAVE_FAILED);
        }

        return Result<DefaultResponse<TaskItemDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>
    /// Applies a sparse partial update to an existing TaskItem. Only the non-null fields on the patch
    /// are changed; everything else is left intact. Delegates the merge to the aggregate's own
    /// <see cref="TaskItem.Update"/> (which already ignores null arguments), so PATCH reuses the same
    /// domain invariants as PUT without forcing the caller to resend the whole aggregate.
    /// </summary>
    public async Task<Result<DefaultResponse<TaskItemDto>>> PatchAsync(
        Guid id, TaskItemPatchDto patch, long? expectedVersion, CancellationToken ct = default)
    {
        // If-Match: * re-reads and applies again when it loses a race (D-073); a concrete version keeps its 412.
        var result = await ConcurrencyRetry.RunAsync(repoTrxn, expectedVersion, nameof(TaskItem), id,
            attemptCt => PatchOnceAsync(id, patch, expectedVersion, attemptCt), ct);
        if (result.IsSuccess && result.Value!.Item is not null) await InvalidateTaskSnapshotsAsync(ct);
        return result;
    }

    /// <summary>One read, merge and save of <see cref="PatchAsync"/>; run again on a lost wildcard race.</summary>
    private async Task<Result<DefaultResponse<TaskItemDto>>> PatchOnceAsync(
        Guid id, TaskItemPatchDto patch, long? expectedVersion, CancellationToken ct)
    {
        var entity = await repoTrxn.GetTaskItemAsync(TaskItemId.From(id), ct: ct);
        if (entity == null)
            return Result<DefaultResponse<TaskItemDto>>.Success(new DefaultResponse<TaskItemDto> { Item = null });

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            RequestTenantId, RequestRoles, entity.TenantId.Value,
            "TaskItem:Patch", nameof(TaskItem), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

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
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.TaskItemPatchFailed(ex, id);
            return Result<DefaultResponse<TaskItemDto>>.Failure(ErrorConstants.ERROR_SAVE_FAILED);
        }

        return Result<DefaultResponse<TaskItemDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task<Result> DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default)
    {
        // If-Match: * re-reads and deletes again when it loses a race (D-073); a concrete version keeps its 412.
        var (result, deleted) = await ConcurrencyRetry.RunAsync(repoTrxn, expectedVersion, nameof(TaskItem), id,
            attemptCt => DeleteOnceAsync(id, expectedVersion, attemptCt), ct);
        if (deleted) await InvalidateTaskSnapshotsAsync(ct);
        return result;
    }

    /// <summary>One read, delete and save of <see cref="DeleteAsync"/>; <c>Deleted</c> is true when a row was removed.</summary>
    private async Task<(Result Result, bool Deleted)> DeleteOnceAsync(Guid id, long? expectedVersion, CancellationToken ct)
    {
        var entity = await repoTrxn.GetTaskItemAsync(TaskItemId.From(id), ct: ct);
        // Deleting an id that is already gone stays 204: the caller's desired state is reached, and a
        // 412 here would make a safe retry look like a conflict.
        if (entity == null) return (Result.Success(), false);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            RequestTenantId, RequestRoles, entity.TenantId.Value,
            "TaskItem:Delete", nameof(TaskItem), entity.Id.Value);
        if (boundary.IsFailure) return (Result.Failure(boundary.ErrorMessage!), false);

        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

        repoTrxn.Delete(entity);

        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.TaskItemDeleteFailed(ex, id);
            return (Result.Failure(ErrorConstants.ERROR_SAVE_FAILED), false);
        }

        return (Result.Success(), true);
    }

    #region Nested children (mutated through the aggregate root - GR-15)

    /// <summary>Saves the tracked aggregate graph and maps failures to a Result.</summary>
    private async Task<Result> SaveAggregateAsync(string errorMessage, CancellationToken ct, params object?[] args)
    {
        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);
            return Result.Success();
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.AggregateSaveFailed(ex, errorMessage, args);
            return Result.Failure(ErrorConstants.ERROR_SAVE_FAILED);
        }
    }

    /// <summary>
    /// Saves a child add (D-073): a write failure after which <paramref name="callerKeyStored"/> finds the caller's key
    /// stored is a lost race for the retry; any other failure maps to a Result like <see cref="SaveAggregateAsync"/>.
    /// </summary>
    private async Task<Result> SaveAddAsync(
        Func<CancellationToken, Task<bool>>? callerKeyStored, string errorMessage, CancellationToken ct, params object?[] args)
    {
        try
        {
            await repoTrxn.SaveChildAddAsync(callerKeyStored, ct);
            return Result.Success();
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.AggregateSaveFailed(ex, errorMessage, args);
            return Result.Failure(ErrorConstants.ERROR_SAVE_FAILED);
        }
    }

    /// <summary>Loads the aggregate root (no children) and enforces the caller tenant boundary.</summary>
    private Task<(TaskItem? Entity, string? Error)> LoadRootAsync(Guid taskItemId, string operation, CancellationToken ct) =>
        TaskItemChildLoader.LoadRootAsync(
            repoTrxn, tenantBoundaryValidator, logger, RequestTenantId, RequestRoles, taskItemId, operation, ct);

    /// <summary>Adds a comment to a TaskItem through the aggregate root.</summary>
    public async Task<Result<DefaultResponse<CommentDto>>> AddCommentAsync(Guid taskItemId, CommentDto comment, CancellationToken ct = default)
    {
        var idCheck = UuidV7.ValidateCallerId(comment.Id);
        if (idCheck.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(idCheck.ErrorMessage!);

        // An add carries no If-Match, so the caller's intent wins a race with another write to the aggregate
        // (D-031 bumps the root on every child write) or with a same-key add: a lost save re-reads and decides
        // again, and a race lost on every attempt is 409 (D-073).
        return await ConcurrencyRetry.RunAsync(repoTrxn, nameof(Comment), taskItemId, async attemptCt =>
        {
            var (entity, error) = await LoadRootAsync(taskItemId, "TaskItem:AddComment", attemptCt);
            if (error is not null) return Result<DefaultResponse<CommentDto>>.Failure(error);
            if (entity is null) return Result<DefaultResponse<CommentDto>>.Success(new DefaultResponse<CommentDto> { Item = null });

            if (comment.Id is Guid callerId && callerId != Guid.Empty)
            {
                var existing = await TaskItemChildLoader.LoadCommentAsync(repoTrxn, taskItemId, callerId, attemptCt);
                if (existing is not null)
                {
                    var existingDto = existing.ToDto();
                    if (!IdempotentCreateGuard.IsEquivalent(existingDto, comment))
                        throw new ConflictException(nameof(Comment), callerId.ToString());

                    return Result<DefaultResponse<CommentDto>>.Success(
                        new DefaultResponse<CommentDto> { Item = existingDto, IsReplay = true, AggregateVersion = entity.Version });
                }
            }

            var addResult = entity.AddComment(comment.Body, DomainId.FromNullable<CommentId>(comment.Id));
            if (addResult.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(addResult.ErrorMessage!);

            var save = await SaveAddAsync(
                comment.Id is Guid id && id != Guid.Empty
                    ? async t => await TaskItemChildLoader.LoadCommentAsync(repoTrxn, taskItemId, id, t) is not null
                    : null,
                "Error adding Comment to TaskItem {Id}", attemptCt, taskItemId);
            if (save.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(save.ErrorMessage!);

            return Result<DefaultResponse<CommentDto>>.Success(
                new DefaultResponse<CommentDto> { Item = addResult.Value!.ToDto(), AggregateVersion = entity.Version });
        }, ct);
    }

    /// <summary>Updates a comment owned by a TaskItem through the aggregate root.</summary>
    public async Task<Result<DefaultResponse<CommentDto>>> UpdateCommentAsync(Guid taskItemId, Guid commentId, CommentDto comment, long? expectedVersion, CancellationToken ct = default)
    {
        // If-Match: * re-reads the root and applies again when it loses a race (D-073); a concrete root
        // version keeps its 412.
        return await ConcurrencyRetry.RunAsync(repoTrxn, expectedVersion, nameof(TaskItem), taskItemId, async attemptCt =>
        {
            var (entity, error) = await LoadRootAsync(taskItemId, "TaskItem:UpdateComment", attemptCt);
            if (error is not null) return Result<DefaultResponse<CommentDto>>.Failure(error);
            if (entity is null) return Result<DefaultResponse<CommentDto>>.Success(new DefaultResponse<CommentDto> { Item = null });

            // Child writes carry the ROOT ETag (D-031).
            ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

            var target = await TaskItemChildLoader.LoadCommentAsync(repoTrxn, taskItemId, commentId, attemptCt);
            if (target is null) return Result<DefaultResponse<CommentDto>>.Success(new DefaultResponse<CommentDto> { Item = null });

            var updateResult = target.Update(comment.Body);
            if (updateResult.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(updateResult.ErrorMessage!);
            entity.MarkChildMutated();

            var save = await SaveAggregateAsync("Error updating Comment {CommentId} on TaskItem {Id}", attemptCt, commentId, taskItemId);
            if (save.IsFailure) return Result<DefaultResponse<CommentDto>>.Failure(save.ErrorMessage!);

            return Result<DefaultResponse<CommentDto>>.Success(
                new DefaultResponse<CommentDto> { Item = target.ToDto(), AggregateVersion = entity.Version });
        }, ct);
    }

    /// <summary>Removes a comment from a TaskItem through the aggregate root.</summary>
    public async Task<Result> RemoveCommentAsync(Guid taskItemId, Guid commentId, long? expectedVersion, CancellationToken ct = default)
    {
        // If-Match: * re-reads the root and applies again when it loses a race (D-073); a concrete root
        // version keeps its 412.
        return await ConcurrencyRetry.RunAsync(repoTrxn, expectedVersion, nameof(TaskItem), taskItemId, async attemptCt =>
        {
            var (entity, error) = await LoadRootAsync(taskItemId, "TaskItem:RemoveComment", attemptCt);
            if (error is not null) return Result.Failure(error);
            if (entity is null) return Result.Success();

            ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

            var target = await TaskItemChildLoader.LoadCommentAsync(repoTrxn, taskItemId, commentId, attemptCt);
            if (target is not null)
            {
                // Children are not loaded on the root, so the removal goes through the entity overload and
                // the repository delete instead of the collection.
                entity.RemoveComment(target);
                repoTrxn.DeleteChild(target);
            }

            return await SaveAggregateAsync("Error removing Comment {CommentId} from TaskItem {Id}", attemptCt, commentId, taskItemId);
        }, ct);
    }

    /// <summary>Adds a checklist item to a TaskItem through the aggregate root.</summary>
    public async Task<Result<DefaultResponse<ChecklistItemDto>>> AddChecklistItemAsync(Guid taskItemId, ChecklistItemDto checklistItem, CancellationToken ct = default)
    {
        var idCheck = UuidV7.ValidateCallerId(checklistItem.Id);
        if (idCheck.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(idCheck.ErrorMessage!);

        // An add carries no If-Match, so the caller's intent wins a race with another write to the aggregate
        // (D-031 bumps the root on every child write) or with a same-key add: a lost save re-reads and decides
        // again, and a race lost on every attempt is 409 (D-073).
        return await ConcurrencyRetry.RunAsync(repoTrxn, nameof(ChecklistItem), taskItemId, async attemptCt =>
        {
            var (entity, error) = await LoadRootAsync(taskItemId, "TaskItem:AddChecklistItem", attemptCt);
            if (error is not null) return Result<DefaultResponse<ChecklistItemDto>>.Failure(error);
            if (entity is null) return Result<DefaultResponse<ChecklistItemDto>>.Success(new DefaultResponse<ChecklistItemDto> { Item = null });

            if (checklistItem.Id is Guid callerId && callerId != Guid.Empty)
            {
                var existing = await TaskItemChildLoader.LoadChecklistItemAsync(repoTrxn, taskItemId, callerId, attemptCt);
                if (existing is not null)
                {
                    var existingDto = existing.ToDto();
                    if (!IdempotentCreateGuard.IsEquivalent(existingDto, checklistItem))
                        throw new ConflictException(nameof(ChecklistItem), callerId.ToString());

                    return Result<DefaultResponse<ChecklistItemDto>>.Success(
                        new DefaultResponse<ChecklistItemDto> { Item = existingDto, IsReplay = true, AggregateVersion = entity.Version });
                }
            }

            var addResult = entity.AddChecklistItem(
                checklistItem.Title, checklistItem.SortOrder, DomainId.FromNullable<ChecklistItemId>(checklistItem.Id));
            if (addResult.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(addResult.ErrorMessage!);
            if (checklistItem.IsCompleted) addResult.Value!.Update(isCompleted: true);

            var save = await SaveAddAsync(
                checklistItem.Id is Guid id && id != Guid.Empty
                    ? async t => await TaskItemChildLoader.LoadChecklistItemAsync(repoTrxn, taskItemId, id, t) is not null
                    : null,
                "Error adding ChecklistItem to TaskItem {Id}", attemptCt, taskItemId);
            if (save.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(save.ErrorMessage!);

            return Result<DefaultResponse<ChecklistItemDto>>.Success(
                new DefaultResponse<ChecklistItemDto> { Item = addResult.Value!.ToDto(), AggregateVersion = entity.Version });
        }, ct);
    }

    /// <summary>Updates a checklist item owned by a TaskItem through the aggregate root.</summary>
    public async Task<Result<DefaultResponse<ChecklistItemDto>>> UpdateChecklistItemAsync(Guid taskItemId, Guid checklistItemId, ChecklistItemDto checklistItem, long? expectedVersion, CancellationToken ct = default)
    {
        // If-Match: * re-reads the root and applies again when it loses a race (D-073); a concrete root
        // version keeps its 412.
        return await ConcurrencyRetry.RunAsync(repoTrxn, expectedVersion, nameof(TaskItem), taskItemId, async attemptCt =>
        {
            var (entity, error) = await LoadRootAsync(taskItemId, "TaskItem:UpdateChecklistItem", attemptCt);
            if (error is not null) return Result<DefaultResponse<ChecklistItemDto>>.Failure(error);
            if (entity is null) return Result<DefaultResponse<ChecklistItemDto>>.Success(new DefaultResponse<ChecklistItemDto> { Item = null });

            ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

            var target = await TaskItemChildLoader.LoadChecklistItemAsync(repoTrxn, taskItemId, checklistItemId, attemptCt);
            if (target is null) return Result<DefaultResponse<ChecklistItemDto>>.Success(new DefaultResponse<ChecklistItemDto> { Item = null });

            var updateResult = target.Update(checklistItem.Title, checklistItem.IsCompleted, checklistItem.SortOrder);
            if (updateResult.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(updateResult.ErrorMessage!);
            entity.MarkChildMutated();

            var save = await SaveAggregateAsync("Error updating ChecklistItem {ChecklistItemId} on TaskItem {Id}", attemptCt, checklistItemId, taskItemId);
            if (save.IsFailure) return Result<DefaultResponse<ChecklistItemDto>>.Failure(save.ErrorMessage!);

            return Result<DefaultResponse<ChecklistItemDto>>.Success(
                new DefaultResponse<ChecklistItemDto> { Item = target.ToDto(), AggregateVersion = entity.Version });
        }, ct);
    }

    /// <summary>Removes a checklist item from a TaskItem through the aggregate root.</summary>
    public async Task<Result> RemoveChecklistItemAsync(Guid taskItemId, Guid checklistItemId, long? expectedVersion, CancellationToken ct = default)
    {
        // If-Match: * re-reads the root and applies again when it loses a race (D-073); a concrete root
        // version keeps its 412.
        return await ConcurrencyRetry.RunAsync(repoTrxn, expectedVersion, nameof(TaskItem), taskItemId, async attemptCt =>
        {
            var (entity, error) = await LoadRootAsync(taskItemId, "TaskItem:RemoveChecklistItem", attemptCt);
            if (error is not null) return Result.Failure(error);
            if (entity is null) return Result.Success();

            ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

            var target = await TaskItemChildLoader.LoadChecklistItemAsync(repoTrxn, taskItemId, checklistItemId, attemptCt);
            if (target is not null)
            {
                entity.RemoveChecklistItem(target);
                repoTrxn.DeleteChild(target);
            }

            return await SaveAggregateAsync("Error removing ChecklistItem {ChecklistItemId} from TaskItem {Id}", attemptCt, checklistItemId, taskItemId);
        }, ct);
    }

    /// <summary>Associates an existing Tag with a TaskItem through the aggregate root.</summary>
    public async Task<Result<DefaultResponse<TaskItemTagDto>>> AssociateTagAsync(Guid taskItemId, Guid tagId, CancellationToken ct = default)
    {
        // An add carries no If-Match, so the caller's intent wins a race with another write to the aggregate
        // (D-031 bumps the root on every child write) or with a same-key add: a lost save re-reads and decides
        // again, and a race lost on every attempt is 409 (D-073).
        return await ConcurrencyRetry.RunAsync(repoTrxn, nameof(TaskItemTag), taskItemId, async attemptCt =>
        {
            var (entity, error) = await LoadRootAsync(taskItemId, "TaskItem:AssociateTag", attemptCt);
            if (error is not null) return Result<DefaultResponse<TaskItemTagDto>>.Failure(error);
            if (entity is null) return Result<DefaultResponse<TaskItemTagDto>>.Success(new DefaultResponse<TaskItemTagDto> { Item = null });

            // Association is idempotent by tag id: an existing row is returned rather than duplicated.
            var existing = await TaskItemChildLoader.LoadTaskItemTagAsync(repoTrxn, taskItemId, tagId, attemptCt);
            if (existing is not null)
                return Result<DefaultResponse<TaskItemTagDto>>.Success(
                    new DefaultResponse<TaskItemTagDto> { Item = existing.ToDto(), IsReplay = true, AggregateVersion = entity.Version });

            var associateResult = entity.AssociateTag(TagId.From(tagId));
            if (associateResult.IsFailure) return Result<DefaultResponse<TaskItemTagDto>>.Failure(associateResult.ErrorMessage!);

            var save = await SaveAddAsync(
                async t => await TaskItemChildLoader.LoadTaskItemTagAsync(repoTrxn, taskItemId, tagId, t) is not null,
                "Error associating Tag {TagId} with TaskItem {Id}", attemptCt, tagId, taskItemId);
            if (save.IsFailure) return Result<DefaultResponse<TaskItemTagDto>>.Failure(save.ErrorMessage!);

            return Result<DefaultResponse<TaskItemTagDto>>.Success(
                new DefaultResponse<TaskItemTagDto> { Item = associateResult.Value!.ToDto(), AggregateVersion = entity.Version });
        }, ct);
    }

    /// <summary>Removes a Tag association from a TaskItem through the aggregate root.</summary>
    public async Task<Result> RemoveTagAsync(Guid taskItemId, Guid tagId, long? expectedVersion, CancellationToken ct = default)
    {
        // If-Match: * re-reads the root and applies again when it loses a race (D-073); a concrete root
        // version keeps its 412.
        return await ConcurrencyRetry.RunAsync(repoTrxn, expectedVersion, nameof(TaskItem), taskItemId, async attemptCt =>
        {
            var (entity, error) = await LoadRootAsync(taskItemId, "TaskItem:RemoveTag", attemptCt);
            if (error is not null) return Result.Failure(error);
            if (entity is null) return Result.Success();

            ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

            var target = await TaskItemChildLoader.LoadTaskItemTagAsync(repoTrxn, taskItemId, tagId, attemptCt);
            if (target is not null)
            {
                entity.RemoveTag(target);
                repoTrxn.DeleteChild(target);
            }

            return await SaveAggregateAsync("Error removing Tag {TagId} from TaskItem {Id}", attemptCt, tagId, taskItemId);
        }, ct);
    }

    #endregion
}
