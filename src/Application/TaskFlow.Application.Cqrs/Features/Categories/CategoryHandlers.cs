using EF.Cache;
using EF.Tenancy;
using EF.Domain.Contracts;
using EF.Data.Contracts;
using EF.Common.Contracts;
using EF.CQRS.Abstractions;
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

namespace TaskFlow.Application.Cqrs.Features.Categories;

/// <summary>Handles search categories work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class SearchCategoriesHandler(
    IRequestContext<string, Guid?> requestContext,
    ICategoryRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<SearchCategoriesQuery, PagedResponse<CategoryDto>>
{
    /// <summary>Handles search categories requests and returns the application result.</summary>
    public async Task<PagedResponse<CategoryDto>> HandleAsync(SearchCategoriesQuery query, CancellationToken ct = default)
    {
        var request = query.Request;
        request.Filter = tenantBoundaryValidator.EnforceTenantFilter(request.Filter, requestContext.TenantId, requestContext.Roles, "CategorySearch");
        return await repoQuery.SearchCategoriesAsync(request, query.IncludeTotal, ct);
    }
}

/// <summary>Handles get category by ID work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class GetCategoryByIdHandler(
    IRequestContext<string, Guid?> requestContext,
    ICategoryRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<GetCategoryByIdQuery, Result<DefaultResponse<CategoryDto>>>
{
    /// <summary>Handles get category by ID requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<CategoryDto>>> HandleAsync(GetCategoryByIdQuery query, CancellationToken ct = default)
    {
        var entity = await repoQuery.GetCategoryAsync(CategoryId.From(query.Id), ct);
        if (entity is null) return Result<DefaultResponse<CategoryDto>>.None();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Category:Get", nameof(Category), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(boundary.ErrorMessage!);

        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles create category work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class CreateCategoryHandler(
    ILogger<CreateCategoryHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ICategoryRepositoryTrxn repoTrxn,
    ICategoryRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator,
    ITypedCache cache)
    : IRequestHandler<CreateCategoryCommand, Result<DefaultResponse<CategoryDto>>>
{
    /// <summary>Handles create category requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<CategoryDto>>> HandleAsync(CreateCategoryCommand command, CancellationToken ct = default)
    {
        var dto = command.Request.Item;
        dto.TenantId = requestContext.TenantId ?? Guid.Empty;

        var validation = CategoryStructureValidator.ValidateCreate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(validation.Errors);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, dto.TenantId,
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

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error creating Category", ct);
        if (save.IsFailure)
        {
            // D-033: a concurrent create with the same id passed the existence check too and won the insert.
            // Re-read on the query context (this one still tracks the failed insert): the winner makes this a
            // replay or a 409. Absent (or not yet replicated) means the save failed for another reason.
            if (dto.Id is Guid racedId && racedId != Guid.Empty
                && await repoQuery.GetCategoryAsync(CategoryId.From(racedId), ct) is { } raced)
            {
                return Result<DefaultResponse<CategoryDto>>.Success(IdempotentCreateGuard.ReplayOrThrow(
                    raced.ToDto(), dto, IdempotentCreateGuard.IsEquivalent, nameof(Category), racedId));
            }

            return Result<DefaultResponse<CategoryDto>>.Failure(save.ErrorMessage!);
        }

        await cache.RemoveByTagAsync(HandlerHelpers.EntityTag(requestContext.TenantId, nameof(Category)), ct);
        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles update category work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class UpdateCategoryHandler(
    ILogger<UpdateCategoryHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ICategoryRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator,
    ITypedCache cache)
    : IRequestHandler<UpdateCategoryCommand, Result<DefaultResponse<CategoryDto>>>
{
    /// <summary>Handles update category requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<CategoryDto>>> HandleAsync(UpdateCategoryCommand command, CancellationToken ct = default)
    {
        var dto = command.Request.Item;
        dto.TenantId = requestContext.TenantId ?? Guid.Empty;

        var validation = CategoryStructureValidator.ValidateUpdate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(validation.Errors);

        var entity = await repoTrxn.GetCategoryAsync(CategoryId.From(dto.Id!.Value), ct);
        if (entity is null)
        {
            return HandlerHelpers.NotFoundResponse<CategoryDto>();
        }

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Category:Update", nameof(Category), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(Category), entity.Id.Value);

        var tenantChangeCheck = tenantBoundaryValidator.PreventTenantChange(
            entity.TenantId.Value, dto.TenantId, nameof(Category), entity.Id.Value);
        if (tenantChangeCheck.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(tenantChangeCheck.ErrorMessage!);

        var updateResult = entity.Update(
            dto.Name, dto.Description, dto.SortOrder, dto.IsActive,
            DomainId.FromNullable<CategoryId>(dto.ParentCategoryId));
        if (updateResult.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(updateResult.ErrorMessage!);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error updating Category {Id}", ct, dto.Id);
        if (save.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(save.ErrorMessage!);

        await cache.RemoveByTagAsync(HandlerHelpers.EntityTag(requestContext.TenantId, nameof(Category)), ct);
        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles delete category work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class DeleteCategoryHandler(
    ILogger<DeleteCategoryHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ICategoryRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator,
    ITypedCache cache)
    : IRequestHandler<DeleteCategoryCommand, Result>
{
    /// <summary>Handles delete category requests and returns the application result.</summary>
    public async Task<Result> HandleAsync(DeleteCategoryCommand command, CancellationToken ct = default)
    {
        var entity = await repoTrxn.GetCategoryAsync(CategoryId.From(command.Id), ct);
        if (entity is null) return Result.Success();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Category:Delete", nameof(Category), entity.Id.Value);
        if (boundary.IsFailure) return Result.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(Category), entity.Id.Value);

        // Composite FK (TenantId, CategoryId) cannot cascade to SetNull; detach the tenant's tasks first (D-022).
        await repoTrxn.ClearCategoryFromTaskItemsAsync(entity.Id, ct);
        repoTrxn.Delete(entity);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error deleting Category {Id}", ct, command.Id);
        if (save.IsFailure) return save;

        await cache.RemoveByTagAsync(HandlerHelpers.EntityTag(requestContext.TenantId, nameof(Category)), ct);
        return Result.Success();
    }
}
