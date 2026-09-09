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
    ITaskFlowCache cache) : ITagService
{
    private Guid? RequestTenantId => requestContext.TenantId;
    private IReadOnlyCollection<string> RequestRoles => requestContext.Roles;
    private bool IsGlobalAdmin => RequestRoles.Contains(AppConstants.ROLE_GLOBAL_ADMIN);

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
        if (!IsGlobalAdmin)
        {
            request.Filter ??= new();
            if (request.Filter.TenantId is Guid supplied && supplied != RequestTenantId)
            {
                logger.LogTenantFilterManipulation("TagSearch", RequestTenantId, supplied);
            }
            request.Filter.TenantId = RequestTenantId;
        }
        return await repoQuery.SearchTagsAsync(request, includeTotal, ct);
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Result<DefaultResponse<TagDto>>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await repoQuery.GetTagAsync(DomainId.From<TagId>(id), ct);
        if (entity == null) return Result<DefaultResponse<TagDto>>.None();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, entity.TenantId.Value,
            "Tag:Get", nameof(Tag), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<TagDto>>.Failure(boundary.ErrorMessage!);

        return Result<DefaultResponse<TagDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public async Task<Result<DefaultResponse<TagDto>>> CreateAsync(
        DefaultRequest<TagDto> request, CancellationToken ct = default)
    {
        var dto = request.Item;
        dto.TenantId = RequestTenantId ?? Guid.Empty;

        var validation = TagStructureValidator.ValidateCreate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<TagDto>>.Failure(validation.Errors);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, dto.TenantId,
            "Tag:Create", nameof(Tag));
        if (boundary.IsFailure) return Result<DefaultResponse<TagDto>>.Failure(boundary.ErrorMessage!);

        // D-033: the row itself is the idempotency record for a caller-supplied UUIDv7 id.
        if (dto.Id is Guid callerId && callerId != Guid.Empty)
        {
            var existing = await repoTrxn.GetAsync(DomainId.From<TagId>(callerId), ct);
            if (existing is not null)
            {
                var existingDto = existing.ToDto();
                if (!IdempotentCreateGuard.IsEquivalent(existingDto, dto))
                    throw new IdempotentCreateConflictException(nameof(Tag), callerId);

                return Result<DefaultResponse<TagDto>>.Success(
                    new DefaultResponse<TagDto> { Item = existingDto, IsReplay = true });
            }
        }

        var entityResult = dto.ToEntity(dto.TenantId);
        if (entityResult.IsFailure) return Result<DefaultResponse<TagDto>>.Failure(entityResult.ErrorMessage!);

        var entity = entityResult.Value!;
        repoTrxn.Create(ref entity);

        try
        {
            await ConcurrencyGuard.SaveAsync(repoTrxn, ct);
        }
        catch (Exception ex) when (!ConcurrencyGuard.IsConcurrencyFailure(ex))
        {
            logger.TagCreateFailed(ex);
            return Result<DefaultResponse<TagDto>>.Failure(ex.GetBaseException().Message);
        }

        await InvalidateMetadataAsync(ct);
        return Result<DefaultResponse<TagDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    public async Task<Result<DefaultResponse<TagDto>>> UpdateAsync(
        DefaultRequest<TagDto> request, long? expectedVersion, CancellationToken ct = default)
    {
        var dto = request.Item;
        dto.TenantId = RequestTenantId ?? Guid.Empty;

        var validation = TagStructureValidator.ValidateUpdate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<TagDto>>.Failure(validation.Errors);

        var entity = await repoTrxn.GetAsync(DomainId.From<TagId>(dto.Id!.Value), ct);
        if (entity == null)
            return Result<DefaultResponse<TagDto>>.Success(new DefaultResponse<TagDto> { Item = null });

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, entity.TenantId.Value,
            "Tag:Update", nameof(Tag), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<TagDto>>.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(Tag), entity.Id.Value);

        var tenantChangeCheck = tenantBoundaryValidator.PreventTenantChange(
            logger, entity.TenantId.Value, dto.TenantId, nameof(Tag), entity.Id.Value);
        if (tenantChangeCheck.IsFailure) return Result<DefaultResponse<TagDto>>.Failure(tenantChangeCheck.ErrorMessage!);

        var updateResult = entity.Update(dto.Name, dto.Color);
        if (updateResult.IsFailure) return Result<DefaultResponse<TagDto>>.Failure(updateResult.ErrorMessage!);

        try
        {
            await ConcurrencyGuard.SaveAsync(repoTrxn, ct);
        }
        catch (Exception ex) when (!ConcurrencyGuard.IsConcurrencyFailure(ex))
        {
            logger.TagUpdateFailed(ex, dto.Id);
            return Result<DefaultResponse<TagDto>>.Failure(ex.GetBaseException().Message);
        }

        await InvalidateMetadataAsync(ct);
        return Result<DefaultResponse<TagDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task<Result> DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default)
    {
        var entity = await repoTrxn.GetAsync(DomainId.From<TagId>(id), ct);
        if (entity == null) return Result.Success();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, entity.TenantId.Value,
            "Tag:Delete", nameof(Tag), entity.Id.Value);
        if (boundary.IsFailure) return Result.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(Tag), entity.Id.Value);

        repoTrxn.Delete(entity);

        try
        {
            await ConcurrencyGuard.SaveAsync(repoTrxn, ct);
        }
        catch (Exception ex) when (!ConcurrencyGuard.IsConcurrencyFailure(ex))
        {
            logger.TagDeleteFailed(ex, id);
            return Result.Failure(ex.GetBaseException().Message);
        }

        await InvalidateMetadataAsync(ct);
        return Result.Success();
    }
}
