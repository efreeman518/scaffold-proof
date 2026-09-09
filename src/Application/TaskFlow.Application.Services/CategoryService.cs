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

/// <summary>Coordinates category application use cases with validation, tenant checks, repositories, and response shaping.</summary>
internal class CategoryService(
    ILogger<CategoryService> logger,
    IRequestContext<string, Guid?> requestContext,
    ICategoryRepositoryTrxn repoTrxn,
    ICategoryRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator,
    ITaskFlowCache cache) : ICategoryService
{
    private Guid? RequestTenantId => requestContext.TenantId;
    private IReadOnlyCollection<string> RequestRoles => requestContext.Roles;
    private bool IsGlobalAdmin => RequestRoles.Contains(AppConstants.ROLE_GLOBAL_ADMIN);

    #region Helpers

    /// <summary>Builds response from current configuration and inputs.</summary>
    private static DefaultResponse<CategoryDto> BuildResponse(CategoryDto dto) =>
        new() { Item = dto, TenantInfo = null };

    /// <summary>
    /// Evicts the tenant metadata snapshot after a successful commit. Evicting first would let a
    /// concurrent read repopulate the entry from the pre-commit state and leave it wrong until it expires.
    /// </summary>
    private Task InvalidateMetadataAsync(CancellationToken ct) =>
        cache.RemoveByTagAsync(CacheTags.Entity(RequestTenantId ?? Guid.Empty, CacheTags.Category), ct);

    #endregion

    /// <summary>Searches search and returns filtered results for callers.</summary>
    public async Task<PagedResponse<CategoryDto>> SearchAsync(
        SearchRequest<CategorySearchFilter> request, bool includeTotal = false, CancellationToken ct = default)
    {
        if (!IsGlobalAdmin)
        {
            request.Filter ??= new();
            if (request.Filter.TenantId is Guid supplied && supplied != RequestTenantId)
            {
                logger.LogTenantFilterManipulation("CategorySearch", RequestTenantId, supplied);
            }
            request.Filter.TenantId = RequestTenantId;
        }
        return await repoQuery.SearchCategoriesAsync(request, includeTotal, ct);
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Result<DefaultResponse<CategoryDto>>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await repoQuery.GetCategoryAsync(DomainId.From<CategoryId>(id), ct);
        if (entity == null) return Result<DefaultResponse<CategoryDto>>.None();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, entity.TenantId.Value,
            "Category:Get", nameof(Category), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(boundary.ErrorMessage!);

        return Result<DefaultResponse<CategoryDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public async Task<Result<DefaultResponse<CategoryDto>>> CreateAsync(
        DefaultRequest<CategoryDto> request, CancellationToken ct = default)
    {
        var dto = request.Item;
        dto.TenantId = RequestTenantId ?? Guid.Empty;

        var validation = CategoryStructureValidator.ValidateCreate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(validation.Errors);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, dto.TenantId,
            "Category:Create", nameof(Category));
        if (boundary.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(boundary.ErrorMessage!);

        // D-033: the row itself is the idempotency record for a caller-supplied UUIDv7 id.
        if (dto.Id is Guid callerId && callerId != Guid.Empty)
        {
            var existing = await repoTrxn.GetCategoryAsync(DomainId.From<CategoryId>(callerId), ct);
            if (existing is not null)
            {
                var existingDto = existing.ToDto();
                if (!IdempotentCreateGuard.IsEquivalent(existingDto, dto))
                    throw new IdempotentCreateConflictException(nameof(Category), callerId);

                return Result<DefaultResponse<CategoryDto>>.Success(
                    new DefaultResponse<CategoryDto> { Item = existingDto, IsReplay = true });
            }
        }

        var entityResult = dto.ToEntity(dto.TenantId);
        if (entityResult.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(entityResult.ErrorMessage!);

        var entity = entityResult.Value!;
        repoTrxn.Create(ref entity);

        try
        {
            await ConcurrencyGuard.SaveAsync(repoTrxn, ct);
        }
        catch (Exception ex) when (!ConcurrencyGuard.IsConcurrencyFailure(ex))
        {
            logger.CategoryCreateFailed(ex);
            return Result<DefaultResponse<CategoryDto>>.Failure(ex.GetBaseException().Message);
        }

        await InvalidateMetadataAsync(ct);
        return Result<DefaultResponse<CategoryDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    public async Task<Result<DefaultResponse<CategoryDto>>> UpdateAsync(
        DefaultRequest<CategoryDto> request, long? expectedVersion, CancellationToken ct = default)
    {
        var dto = request.Item;
        dto.TenantId = RequestTenantId ?? Guid.Empty;

        var validation = CategoryStructureValidator.ValidateUpdate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(validation.Errors);

        var entity = await repoTrxn.GetCategoryAsync(DomainId.From<CategoryId>(dto.Id!.Value), ct);
        if (entity == null)
            return Result<DefaultResponse<CategoryDto>>.Success(new DefaultResponse<CategoryDto> { Item = null });

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, entity.TenantId.Value,
            "Category:Update", nameof(Category), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(Category), entity.Id.Value);

        var tenantChangeCheck = tenantBoundaryValidator.PreventTenantChange(
            logger, entity.TenantId.Value, dto.TenantId, nameof(Category), entity.Id.Value);
        if (tenantChangeCheck.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(tenantChangeCheck.ErrorMessage!);

        var updateResult = entity.Update(
            dto.Name, dto.Description, dto.SortOrder, dto.IsActive,
            DomainId.FromNullable<CategoryId>(dto.ParentCategoryId));
        if (updateResult.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(updateResult.ErrorMessage!);

        try
        {
            await ConcurrencyGuard.SaveAsync(repoTrxn, ct);
        }
        catch (Exception ex) when (!ConcurrencyGuard.IsConcurrencyFailure(ex))
        {
            logger.CategoryUpdateFailed(ex, dto.Id);
            return Result<DefaultResponse<CategoryDto>>.Failure(ex.GetBaseException().Message);
        }

        await InvalidateMetadataAsync(ct);
        return Result<DefaultResponse<CategoryDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task<Result> DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default)
    {
        var entity = await repoTrxn.GetCategoryAsync(DomainId.From<CategoryId>(id), ct);
        if (entity == null) return Result.Success();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, entity.TenantId.Value,
            "Category:Delete", nameof(Category), entity.Id.Value);
        if (boundary.IsFailure) return Result.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(Category), entity.Id.Value);

        try
        {
            // Composite FK (TenantId, CategoryId) cannot cascade to SetNull; detach the tenant's tasks first (D-022).
            await repoTrxn.ClearCategoryFromTaskItemsAsync(entity.Id, ct);
            repoTrxn.Delete(entity);
            await ConcurrencyGuard.SaveAsync(repoTrxn, ct);
        }
        catch (Exception ex) when (!ConcurrencyGuard.IsConcurrencyFailure(ex))
        {
            logger.CategoryDeleteFailed(ex, id);
            return Result.Failure(ex.GetBaseException().Message);
        }

        await InvalidateMetadataAsync(ct);
        return Result.Success();
    }
}
