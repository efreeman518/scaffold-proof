using EF.Common.Contracts;
using EF.Data.Contracts;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts;
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
    IEntityCacheProvider cache) : ITagService
{
    private Guid? RequestTenantId => requestContext.TenantId;
    private IReadOnlyCollection<string> RequestRoles => requestContext.Roles;
    private bool IsGlobalAdmin => RequestRoles.Contains(AppConstants.ROLE_GLOBAL_ADMIN);

    #region Helpers

    /// <summary>Builds response from current configuration and inputs.</summary>
    private static DefaultResponse<TagDto> BuildResponse(TagDto dto) =>
        new() { Item = dto, TenantInfo = null };

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
            logger.LogError(ex, "Error creating Tag");
            return Result<DefaultResponse<TagDto>>.Failure(ex.GetBaseException().Message);
        }

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
            logger.LogError(ex, "Error updating Tag {Id}", dto.Id);
            return Result<DefaultResponse<TagDto>>.Failure(ex.GetBaseException().Message);
        }

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
            logger.LogError(ex, "Error deleting Tag {Id}", id);
            return Result.Failure(ex.GetBaseException().Message);
        }

        await cache.RemoveAsync($"Tag:{id}", ct);
        return Result.Success();
    }
}
