using EF.Cache;
using EF.Tenancy;
using EF.Domain.Contracts;
using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using EF.Data.Contracts;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Cqrs.Shared;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Model.ValueObjects;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Application.Cqrs.Features.TaskItems;

/// <summary>Handles search task items work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class SearchTaskItemsHandler(
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<SearchTaskItemsQuery, CursorPage<TaskItemDto>>
{
    /// <summary>Handles search task items requests and returns the application result.</summary>
    public async Task<CursorPage<TaskItemDto>> HandleAsync(SearchTaskItemsQuery query, CancellationToken ct = default)
    {
        var request = query.Request;

        // Out-of-range page size and an unusable cursor are caller errors (400), not something to clamp
        // or silently reset to page one - a reset would re-serve rows the caller already read. The cursor
        // half is enforced by the repository, which owns the codec (ERROR_CURSOR_INVALID).
        if (!PageSizeLimits.IsValid(request.PageSize))
            throw new InvalidRequestException(
                string.Format(ErrorConstants.ERROR_PAGE_SIZE_RANGE, PageSizeLimits.Min, PageSizeLimits.Max));

        request.Filter = tenantBoundaryValidator.EnforceTenantFilter(request.Filter, requestContext.TenantId, requestContext.Roles, "TaskItemSearch");
        var tenantId = request.Filter?.TenantId ?? requestContext.TenantId ?? Guid.Empty;

        return await repoQuery.SearchTaskItemsAsync(request, tenantId, ct);
    }
}

/// <summary>Handles get task item by ID work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class GetTaskItemByIdHandler(
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<GetTaskItemByIdQuery, Result<DefaultResponse<TaskItemDto>>>
{
    /// <summary>Handles get task item by ID requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<TaskItemDto>>> HandleAsync(GetTaskItemByIdQuery query, CancellationToken ct = default)
    {
        var entity = await repoQuery.GetTaskItemAsync(TaskItemId.From(query.Id), ct);
        if (entity is null) return Result<DefaultResponse<TaskItemDto>>.None();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "TaskItem:Get", nameof(TaskItem), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(boundary.ErrorMessage!);

        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles create task item work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class CreateTaskItemHandler(
    ILogger<CreateTaskItemHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryTrxn repoTrxn,
    ITaskItemRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator,
    ITypedCache cache)
    : IRequestHandler<CreateTaskItemCommand, Result<DefaultResponse<TaskItemDto>>>
{
    /// <summary>Handles create task item requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<TaskItemDto>>> HandleAsync(CreateTaskItemCommand command, CancellationToken ct = default)
    {
        var dto = command.Request.Item;
        dto.TenantId = requestContext.TenantId ?? Guid.Empty;

        var validation = TaskItemStructureValidator.ValidateCreate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(validation.Errors);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, dto.TenantId,
            "TaskItem:Create", nameof(TaskItem));
        if (boundary.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(boundary.ErrorMessage!);

        // D-033: the row itself is the idempotency record for a caller-supplied UUIDv7 id.
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

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error creating TaskItem", ct);
        if (save.IsFailure)
        {
            // D-033: a concurrent create with the same id passed the existence check too and won the insert.
            // Re-read on the query context (this one still tracks the failed insert): the winner makes this a
            // replay or a 409. Absent (or not yet replicated) means the save failed for another reason.
            if (dto.Id is Guid racedId && racedId != Guid.Empty
                && await repoQuery.GetTaskItemAsync(TaskItemId.From(racedId), ct) is { } raced)
            {
                return Result<DefaultResponse<TaskItemDto>>.Success(IdempotentCreateGuard.ReplayOrThrow(
                    raced.ToDto(), dto, IdempotentCreateGuard.IsEquivalent, nameof(TaskItem), racedId));
            }

            return Result<DefaultResponse<TaskItemDto>>.Failure(save.ErrorMessage!);
        }

        await cache.RemoveByTagAsync(HandlerHelpers.EntityTag(requestContext.TenantId, nameof(TaskItem)), ct);
        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles update task item work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class UpdateTaskItemHandler(
    ILogger<UpdateTaskItemHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator,
    ITypedCache cache)
    : IRequestHandler<UpdateTaskItemCommand, Result<DefaultResponse<TaskItemDto>>>
{
    /// <summary>Handles update task item requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<TaskItemDto>>> HandleAsync(UpdateTaskItemCommand command, CancellationToken ct = default)
    {
        var dto = command.Request.Item;
        dto.TenantId = requestContext.TenantId ?? Guid.Empty;

        var validation = TaskItemStructureValidator.ValidateUpdate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(validation.Errors);

        var entity = await repoTrxn.GetTaskItemAsync(TaskItemId.From(dto.Id!.Value), ct: ct);
        if (entity is null)
        {
            return HandlerHelpers.NotFoundResponse<TaskItemDto>();
        }

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "TaskItem:Update", nameof(TaskItem), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(boundary.ErrorMessage!);

        // After load, before any mutation: a stale caller must not run the status state machine.
        ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

        var tenantChangeCheck = tenantBoundaryValidator.PreventTenantChange(
            entity.TenantId.Value, dto.TenantId, nameof(TaskItem), entity.Id.Value);
        if (tenantChangeCheck.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(tenantChangeCheck.ErrorMessage!);

        // The aggregate raises the status/completed events; the staging interceptor writes them (D-026).
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

        var syncResult = repoTrxn.UpdateFromDto(entity, dto, RelatedDeleteBehavior.RelationshipAndEntity);
        if (syncResult.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(syncResult.ErrorMessage!);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error updating TaskItem {Id}", ct, dto.Id);
        if (save.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(save.ErrorMessage!);

        await cache.RemoveByTagAsync(HandlerHelpers.EntityTag(requestContext.TenantId, nameof(TaskItem)), ct);
        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles delete task item work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class DeleteTaskItemHandler(
    ILogger<DeleteTaskItemHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator,
    ITypedCache cache)
    : IRequestHandler<DeleteTaskItemCommand, Result>
{
    /// <summary>Handles delete task item requests and returns the application result.</summary>
    public async Task<Result> HandleAsync(DeleteTaskItemCommand command, CancellationToken ct = default)
    {
        var entity = await repoTrxn.GetTaskItemAsync(TaskItemId.From(command.Id), ct: ct);
        if (entity is null) return Result.Success();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "TaskItem:Delete", nameof(TaskItem), entity.Id.Value);
        if (boundary.IsFailure) return Result.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

        repoTrxn.Delete(entity);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error deleting TaskItem {Id}", ct, command.Id);
        if (save.IsFailure) return save;

        await cache.RemoveByTagAsync(HandlerHelpers.EntityTag(requestContext.TenantId, nameof(TaskItem)), ct);
        return Result.Success();
    }
}

/// <summary>
/// Handles patch task item work. PATCH parity with the Service style: the sparse merge is delegated to
/// the aggregate's own Update, which already ignores null arguments, so both styles enforce the same
/// invariants without the caller resending the whole aggregate.
/// </summary>
internal sealed class PatchTaskItemHandler(
    ILogger<PatchTaskItemHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ITaskItemRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator,
    ITypedCache cache)
    : IRequestHandler<PatchTaskItemCommand, Result<DefaultResponse<TaskItemDto>>>
{
    /// <summary>Handles patch task item requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<TaskItemDto>>> HandleAsync(PatchTaskItemCommand command, CancellationToken ct = default)
    {
        var entity = await repoTrxn.GetTaskItemAsync(TaskItemId.From(command.Id), ct: ct);
        if (entity is null) return HandlerHelpers.NotFoundResponse<TaskItemDto>();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "TaskItem:Patch", nameof(TaskItem), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(TaskItem), entity.Id.Value);

        var patch = command.Patch;
        var updateResult = entity.Update(
            title: patch.Title,
            description: patch.Description,
            priority: patch.Priority,
            estimatedEffort: patch.EstimatedEffort,
            categoryId: DomainId.FromNullable<CategoryId>(patch.CategoryId),
            parentTaskItemId: DomainId.FromNullable<TaskItemId>(patch.ParentTaskItemId));
        if (updateResult.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(updateResult.ErrorMessage!);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error patching TaskItem {Id}", ct, command.Id);
        if (save.IsFailure) return Result<DefaultResponse<TaskItemDto>>.Failure(save.ErrorMessage!);

        await cache.RemoveByTagAsync(HandlerHelpers.EntityTag(requestContext.TenantId, nameof(TaskItem)), ct);
        return HandlerHelpers.Success(entity.ToDto());
    }
}
