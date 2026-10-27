using EF.Domain.Contracts;
﻿using EF.Cache;
using EF.Tenancy;
using EF.Common.Contracts;
using EF.Data.Contracts;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Application.Services.Rules;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Services;

/// <summary>Coordinates tag application use cases with validation, tenant checks, repositories, and response shaping.</summary>
internal class TagService(
    ILogger<TagService> logger,
    IRequestContext<string, Guid?> requestContext,
    IRepositoryTrxn<Tag, TagId> repoTrxn,
    ITagRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator,
    ITypedCache cache) : ITagService
{
    private Guid? RequestTenantId => requestContext.TenantId;
    private IReadOnlyCollection<string> RequestRoles => requestContext.Roles;

    #region Helpers

    /// <summary>Builds response from current configuration and inputs.</summary>
    private static DefaultResponse<TagDto> BuildResponse(TagDto dto) =>
        new() { Item = dto, TenantInfo = null };

    /// <summary>
    /// Evicts the tenant metadata snapshot after a successful commit. Evicting first would let a
    /// concurrent read repopulate the entry from the pre-commit state and leave it wrong until it expires.
    /// </summary>
    private Task InvalidateMetadataAsync(CancellationToken ct) =>
        cache.RemoveByTagAsync(CacheTags.Entity(RequestTenantId ?? Guid.Empty, CacheTags.Tag), ct);

    #endregion

    /// <summary>Searches search and returns filtered results for callers.</summary>
    public async Task<PagedResponse<TagDto>> SearchAsync(
        SearchRequest<TagSearchFilter> request, bool includeTotal = false, CancellationToken ct = default)
    {
        request.Filter = tenantBoundaryValidator.EnforceTenantFilter(request.Filter, RequestTenantId, RequestRoles, "TagSearch");
        return await repoQuery.SearchTagsAsync(request, includeTotal, ct);
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Result<DefaultResponse<TagDto>>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await repoQuery.GetTagAsync(TagId.From(id), ct);
        if (entity == null) return new NotFoundError(ErrorConstants.ERROR_ITEM_NOTFOUND);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            RequestTenantId, RequestRoles, entity.TenantId.Value,
            "Tag:Get", nameof(Tag), entity.Id.Value);
        if (boundary.TryGetValue(out Error? boundaryError)) return boundaryError;

        return BuildResponse(entity.ToDto());
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public async Task<Result<DefaultResponse<TagDto>>> CreateAsync(
        DefaultRequest<TagDto> request, CancellationToken ct = default)
    {
        var dto = request.Item;
        dto.TenantId = RequestTenantId ?? Guid.Empty;

        var validation = TagStructureValidator.ValidateCreate(dto);
        if (validation.TryGetValue(out Error? validationError)) return validationError;

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            RequestTenantId, RequestRoles, dto.TenantId,
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

        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.TagCreateFailed(ex);

            // D-033: a concurrent create with the same id passed the existence check too and won the insert.
            // Re-read on the query context (this one still tracks the failed insert): the winner makes this a
            // replay or a 409. Absent (or not yet replicated) means the save failed for another reason.
            if (dto.Id is Guid racedId && racedId != Guid.Empty
                && await repoQuery.GetTagAsync(TagId.From(racedId), ct) is { } raced)
            {
                return IdempotentCreateGuard.ReplayOrThrow(
                    raced.ToDto(), dto, IdempotentCreateGuard.IsEquivalent, nameof(Tag), racedId);
            }

            return SaveFailure.ToError(ex);
        }

        await InvalidateMetadataAsync(ct);
        return BuildResponse(entity.ToDto());
    }

    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    public async Task<Result<DefaultResponse<TagDto>>> UpdateAsync(
        DefaultRequest<TagDto> request, long? expectedVersion, CancellationToken ct = default)
    {
        var dto = request.Item;
        dto.TenantId = RequestTenantId ?? Guid.Empty;

        var validation = TagStructureValidator.ValidateUpdate(dto);
        if (validation.TryGetValue(out Error? validationError)) return validationError;

        // If-Match: * re-reads and applies again when it loses a race (D-073); a concrete version keeps its 412.
        var result = await ConcurrencyRetry.RunAsync(repoTrxn, expectedVersion, nameof(Tag), dto.Id!.Value,
            attemptCt => UpdateOnceAsync(dto, expectedVersion, attemptCt), ct);
        if (result is DefaultResponse<TagDto> { Item: not null }) await InvalidateMetadataAsync(ct);
        return result;
    }

    /// <summary>One read, update and save of <see cref="UpdateAsync"/>; run again on a lost wildcard race.</summary>
    private async Task<Result<DefaultResponse<TagDto>>> UpdateOnceAsync(TagDto dto, long? expectedVersion, CancellationToken ct)
    {
        var entity = await repoTrxn.GetAsync(TagId.From(dto.Id!.Value), ct);
        if (entity == null)
            return new DefaultResponse<TagDto> { Item = null };

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            RequestTenantId, RequestRoles, entity.TenantId.Value,
            "Tag:Update", nameof(Tag), entity.Id.Value);
        if (boundary.TryGetValue(out Error? boundaryError)) return boundaryError;

        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(Tag), entity.Id.Value);

        var tenantChangeCheck = tenantBoundaryValidator.PreventTenantChange(
            entity.TenantId.Value, dto.TenantId, nameof(Tag), entity.Id.Value);
        if (tenantChangeCheck.TryGetValue(out Error? tenantChangeCheckError)) return tenantChangeCheckError;

        var updateResult = entity.Update(dto.Name, dto.Color);
        if (updateResult.TryGetValue(out Error? updateResultError)) return updateResultError;

        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.TagUpdateFailed(ex, dto.Id);
            return SaveFailure.ToError(ex);
        }

        return BuildResponse(entity.ToDto());
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task<Result<Unit>> DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default)
    {
        // If-Match: * re-reads and deletes again when it loses a race (D-073); a concrete version keeps its 412.
        var saveSent = false;
        var (result, deleted) = await ConcurrencyRetry.RunAsync(repoTrxn, expectedVersion, nameof(Tag), id,
            attemptCt => DeleteOnceAsync(id, expectedVersion, saveSent, () => saveSent = true, attemptCt), ct);
        if (deleted) await InvalidateMetadataAsync(ct);
        return result;
    }

    /// <summary>One read, delete and save of <see cref="DeleteAsync"/>; <c>Deleted</c> is true when a row was removed.</summary>
    private async Task<(Result<Unit> Result, bool Deleted)> DeleteOnceAsync(
        Guid id, long? expectedVersion, bool saveSent, Action markSaveSent, CancellationToken ct)
    {
        var entity = await repoTrxn.GetAsync(TagId.From(id), ct);
        // D-073: gone on a wildcard retry after an earlier attempt sent its save (a commit that landed but was reported
        // failed, or a competing delete) counts as deleted, so the after-save effects run; gone on the first attempt does not.
        if (entity == null) return (Result.Ok(), saveSent);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            RequestTenantId, RequestRoles, entity.TenantId.Value,
            "Tag:Delete", nameof(Tag), entity.Id.Value);
        if (boundary.TryGetValue(out Error? boundaryError)) return (boundaryError, false);

        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(Tag), entity.Id.Value);

        repoTrxn.Delete(entity);
        markSaveSent();

        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.TagDeleteFailed(ex, id);
            return (SaveFailure.ToError(ex), false);
        }

        return (Result.Ok(), true);
    }
}
