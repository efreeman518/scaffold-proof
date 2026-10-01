using EF.Cache;
using EF.Tenancy;
using EF.Domain.Contracts;
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
    ITypedCache cache) : ICategoryService
{
    private Guid? RequestTenantId => requestContext.TenantId;
    private IReadOnlyCollection<string> RequestRoles => requestContext.Roles;

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
        request.Filter = tenantBoundaryValidator.EnforceTenantFilter(request.Filter, RequestTenantId, RequestRoles, "CategorySearch");
        return await repoQuery.SearchCategoriesAsync(request, includeTotal, ct);
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Result<DefaultResponse<CategoryDto>>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await repoQuery.GetCategoryAsync(CategoryId.From(id), ct);
        if (entity == null) return Result<DefaultResponse<CategoryDto>>.None();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            RequestTenantId, RequestRoles, entity.TenantId.Value,
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
            RequestTenantId, RequestRoles, dto.TenantId,
            "Category:Create", nameof(Category));
        if (boundary.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(boundary.ErrorMessage!);

        // D-033: the row itself is the idempotency record for a caller-supplied UUIDv7 id.
        if (dto.Id is Guid callerId && callerId != Guid.Empty)
        {
            var existing = await repoTrxn.GetCategoryAsync(CategoryId.From(callerId), ct);
            if (existing is not null)
            {
                return Result<DefaultResponse<CategoryDto>>.Success(IdempotentCreateGuard.ReplayOrThrow(
                    existing.ToDto(), dto, IdempotentCreateGuard.IsEquivalent, nameof(Category), callerId));
            }
        }

        var entityResult = dto.ToEntity(dto.TenantId);
        if (entityResult.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(entityResult.ErrorMessage!);

        var entity = entityResult.Value!;
        repoTrxn.Create(ref entity);

        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.CategoryCreateFailed(ex);

            // D-033: a concurrent create with the same id passed the existence check too and won the insert.
            // Re-read on the query context (this one still tracks the failed insert): the winner makes this a
            // replay or a 409. Absent (or not yet replicated) means the save failed for another reason.
            if (dto.Id is Guid racedId && racedId != Guid.Empty
                && await repoQuery.GetCategoryAsync(CategoryId.From(racedId), ct) is { } raced)
            {
                return Result<DefaultResponse<CategoryDto>>.Success(IdempotentCreateGuard.ReplayOrThrow(
                    raced.ToDto(), dto, IdempotentCreateGuard.IsEquivalent, nameof(Category), racedId));
            }

            return Result<DefaultResponse<CategoryDto>>.Failure(ErrorConstants.ERROR_SAVE_FAILED);
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

        // If-Match: * re-reads and applies again when it loses a race (D-073); a concrete version keeps its 412.
        var result = await ConcurrencyRetry.RunAsync(repoTrxn, expectedVersion, nameof(Category), dto.Id!.Value,
            attemptCt => UpdateOnceAsync(dto, expectedVersion, attemptCt), ct);
        if (result.IsSuccess && result.Value!.Item is not null) await InvalidateMetadataAsync(ct);
        return result;
    }

    /// <summary>One read, update and save of <see cref="UpdateAsync"/>; run again on a lost wildcard race.</summary>
    private async Task<Result<DefaultResponse<CategoryDto>>> UpdateOnceAsync(CategoryDto dto, long? expectedVersion, CancellationToken ct)
    {
        var entity = await repoTrxn.GetCategoryAsync(CategoryId.From(dto.Id!.Value), ct);
        if (entity == null)
            return Result<DefaultResponse<CategoryDto>>.Success(new DefaultResponse<CategoryDto> { Item = null });

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            RequestTenantId, RequestRoles, entity.TenantId.Value,
            "Category:Update", nameof(Category), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(Category), entity.Id.Value);

        var tenantChangeCheck = tenantBoundaryValidator.PreventTenantChange(
            entity.TenantId.Value, dto.TenantId, nameof(Category), entity.Id.Value);
        if (tenantChangeCheck.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(tenantChangeCheck.ErrorMessage!);

        var updateResult = entity.Update(
            dto.Name, dto.Description, dto.SortOrder, dto.IsActive,
            DomainId.FromNullable<CategoryId>(dto.ParentCategoryId));
        if (updateResult.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(updateResult.ErrorMessage!);

        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.CategoryUpdateFailed(ex, dto.Id);
            return Result<DefaultResponse<CategoryDto>>.Failure(ErrorConstants.ERROR_SAVE_FAILED);
        }

        return Result<DefaultResponse<CategoryDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task<Result> DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default)
    {
        // If-Match: * re-reads and deletes again when it loses a race (D-073); a concrete version keeps its 412.
        var (result, deleted) = await ConcurrencyRetry.RunAsync(repoTrxn, expectedVersion, nameof(Category), id,
            attemptCt => DeleteOnceAsync(id, expectedVersion, attemptCt), ct);
        if (deleted) await InvalidateMetadataAsync(ct);
        return result;
    }

    /// <summary>One read, delete and save of <see cref="DeleteAsync"/>; <c>Deleted</c> is true when a row was removed.</summary>
    private async Task<(Result Result, bool Deleted)> DeleteOnceAsync(Guid id, long? expectedVersion, CancellationToken ct)
    {
        var entity = await repoTrxn.GetCategoryAsync(CategoryId.From(id), ct);
        if (entity == null) return (Result.Success(), false);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            RequestTenantId, RequestRoles, entity.TenantId.Value,
            "Category:Delete", nameof(Category), entity.Id.Value);
        if (boundary.IsFailure) return (Result.Failure(boundary.ErrorMessage!), false);

        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(Category), entity.Id.Value);

        try
        {
            // Composite FK (TenantId, CategoryId) cannot cascade to SetNull: the repository detaches the tenant's
            // tasks and deletes the row in one unit with one Throw save (D-022), so a failed save detaches nothing.
            await repoTrxn.DeleteCategoryAsync(entity, ct);
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.CategoryDeleteFailed(ex, id);
            return (Result.Failure(ErrorConstants.ERROR_SAVE_FAILED), false);
        }

        return (Result.Success(), true);
    }
}
