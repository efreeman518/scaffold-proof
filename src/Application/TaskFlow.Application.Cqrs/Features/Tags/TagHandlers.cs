using EF.Domain.Contracts;
﻿using EF.Cache;
using EF.Tenancy;
using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using EF.Data.Contracts;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Cqrs.Shared;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Cqrs.Features.Tags;

/// <summary>Handles search tags work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class SearchTagsHandler(
    IRequestContext<string, Guid?> requestContext,
    ITagRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<SearchTagsQuery, PagedResponse<TagDto>>
{
    /// <summary>Handles search tags requests and returns the application result.</summary>
    public async Task<PagedResponse<TagDto>> HandleAsync(SearchTagsQuery query, CancellationToken ct = default)
    {
        var request = query.Request;
        request.Filter = tenantBoundaryValidator.EnforceTenantFilter(request.Filter, requestContext.TenantId, requestContext.Roles, "TagSearch");
        return await repoQuery.SearchTagsAsync(request, query.IncludeTotal, ct);
    }
}

/// <summary>Handles get tag by ID work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class GetTagByIdHandler(
    IRequestContext<string, Guid?> requestContext,
    ITagRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<GetTagByIdQuery, Result<DefaultResponse<TagDto>>>
{
    /// <summary>Handles get tag by ID requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<TagDto>>> HandleAsync(GetTagByIdQuery query, CancellationToken ct = default)
    {
        var entity = await repoQuery.GetTagAsync(TagId.From(query.Id), ct);
        if (entity is null) return new NotFoundError(ErrorConstants.ERROR_ITEM_NOTFOUND);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Tag:Get", nameof(Tag), entity.Id.Value);
        if (boundary.TryGetValue(out Error? boundaryError)) return boundaryError;

        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles create tag work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class CreateTagHandler(
    ILogger<CreateTagHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    IRepositoryTrxn<Tag, TagId> repoTrxn,
    ITagRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator,
    ITypedCache cache)
    : IRequestHandler<CreateTagCommand, Result<DefaultResponse<TagDto>>>
{
    /// <summary>Handles create tag requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<TagDto>>> HandleAsync(CreateTagCommand command, CancellationToken ct = default)
    {
        var dto = command.Request.Item;
        dto.TenantId = requestContext.TenantId ?? Guid.Empty;

        var validation = TagStructureValidator.ValidateCreate(dto);
        if (validation.TryGetValue(out Error? validationError)) return validationError;

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, dto.TenantId,
            "Tag:Create", nameof(Tag));
        if (boundary.TryGetValue(out Error? boundaryError)) return boundaryError;

        // D-033: the row itself is the idempotency record for a caller-supplied UUIDv7 id.
        if (dto.Id is Guid callerId && callerId != Guid.Empty)
        {
            var existing = await repoTrxn.GetAsync(TagId.From(callerId), ct);
            if (existing is not null)
            {
                return IdempotentCreateGuard.ReplayOrThrow(
                    existing.ToDto(), dto, IdempotentCreateGuard.IsEquivalent, nameof(Tag), callerId);
            }
        }

        var entityResult = dto.ToEntity(dto.TenantId);
        if (entityResult.TryGetValue(out Error? entityResultError)) return entityResultError;

        var entity = entityResult.GetValueOrThrow();
        repoTrxn.Create(ref entity);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error creating Tag", ct);
        if (save.TryGetValue(out Error? saveError))
        {
            // D-033: a concurrent create with the same id passed the existence check too and won the insert.
            // Re-read on the query context (this one still tracks the failed insert): the winner makes this a
            // replay or a 409. Absent (or not yet replicated) means the save failed for another reason.
            if (dto.Id is Guid racedId && racedId != Guid.Empty
                && await repoQuery.GetTagAsync(TagId.From(racedId), ct) is { } raced)
            {
                return IdempotentCreateGuard.ReplayOrThrow(
                    raced.ToDto(), dto, IdempotentCreateGuard.IsEquivalent, nameof(Tag), racedId);
            }

            return saveError;
        }

        await cache.RemoveByTagAsync(HandlerHelpers.EntityTag(requestContext.TenantId, nameof(Tag)), ct);
        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles update tag work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class UpdateTagHandler(
    ILogger<UpdateTagHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    IRepositoryTrxn<Tag, TagId> repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator,
    ITypedCache cache)
    : IRequestHandler<UpdateTagCommand, Result<DefaultResponse<TagDto>>>
{
    /// <summary>Handles update tag requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<TagDto>>> HandleAsync(UpdateTagCommand command, CancellationToken ct = default)
    {
        var dto = command.Request.Item;
        dto.TenantId = requestContext.TenantId ?? Guid.Empty;

        var validation = TagStructureValidator.ValidateUpdate(dto);
        if (validation.TryGetValue(out Error? validationError)) return validationError;

        // If-Match: * re-reads and applies again when it loses a race (D-073); a concrete version keeps its 412.
        var result = await ConcurrencyRetry.RunAsync(repoTrxn, command.ExpectedVersion, nameof(Tag), dto.Id!.Value,
            attemptCt => UpdateOnceAsync(dto, command.ExpectedVersion, attemptCt), ct);
        if (result is DefaultResponse<TagDto> { Item: not null })
            await cache.RemoveByTagAsync(HandlerHelpers.EntityTag(requestContext.TenantId, nameof(Tag)), ct);
        return result;
    }

    /// <summary>One read, update and save; run again on a lost wildcard race.</summary>
    private async Task<Result<DefaultResponse<TagDto>>> UpdateOnceAsync(TagDto dto, long? expectedVersion, CancellationToken ct)
    {
        var entity = await repoTrxn.GetAsync(TagId.From(dto.Id!.Value), ct);
        if (entity is null) return new NotFoundError(ErrorConstants.ERROR_ITEM_NOTFOUND);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Tag:Update", nameof(Tag), entity.Id.Value);
        if (boundary.TryGetValue(out Error? boundaryError)) return boundaryError;

        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(Tag), entity.Id.Value);

        var tenantChangeCheck = tenantBoundaryValidator.PreventTenantChange(
            entity.TenantId.Value, dto.TenantId, nameof(Tag), entity.Id.Value);
        if (tenantChangeCheck.TryGetValue(out Error? tenantChangeCheckError)) return tenantChangeCheckError;

        var updateResult = entity.Update(dto.Name, dto.Color);
        if (updateResult.TryGetValue(out Error? updateResultError)) return updateResultError;

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error updating Tag {Id}", ct, dto.Id);
        if (save.TryGetValue(out Error? saveError)) return saveError;

        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles delete tag work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class DeleteTagHandler(
    ILogger<DeleteTagHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    IRepositoryTrxn<Tag, TagId> repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator,
    ITypedCache cache)
    : IRequestHandler<DeleteTagCommand, Result<Unit>>
{
    /// <summary>Handles delete tag requests and returns the application result.</summary>
    public async Task<Result<Unit>> HandleAsync(DeleteTagCommand command, CancellationToken ct = default)
    {
        // If-Match: * re-reads and deletes again when it loses a race (D-073); a concrete version keeps its 412.
        var saveSent = false;
        var (result, deleted) = await ConcurrencyRetry.RunAsync(repoTrxn, command.ExpectedVersion, nameof(Tag), command.Id,
            attemptCt => DeleteOnceAsync(command, saveSent, () => saveSent = true, attemptCt), ct);
        if (deleted) await cache.RemoveByTagAsync(HandlerHelpers.EntityTag(requestContext.TenantId, nameof(Tag)), ct);
        return result;
    }

    /// <summary>One read, delete and save; <c>Deleted</c> is true when a row was removed.</summary>
    private async Task<(Result<Unit> Result, bool Deleted)> DeleteOnceAsync(
        DeleteTagCommand command, bool saveSent, Action markSaveSent, CancellationToken ct)
    {
        var entity = await repoTrxn.GetAsync(TagId.From(command.Id), ct);
        // D-073: gone on a wildcard retry after an earlier attempt sent its save (a commit that landed but was reported
        // failed, or a competing delete) counts as deleted, so the after-save effects run; gone on the first attempt does not.
        if (entity is null) return (Result.Ok(), saveSent);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Tag:Delete", nameof(Tag), entity.Id.Value);
        if (boundary.TryGetValue(out Error? boundaryError)) return (boundaryError, false);

        ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(Tag), entity.Id.Value);

        repoTrxn.Delete(entity);
        markSaveSent();

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error deleting Tag {Id}", ct, command.Id);
        return (save, save.IsSuccess);
    }
}
