using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Cqrs.Shared;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Cqrs.Features.Categories;

/// <summary>Handles search categories work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class SearchCategoriesHandler(
    ILogger<SearchCategoriesHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ICategoryRepositoryQuery repoQuery)
    : IRequestHandler<SearchCategoriesQuery, PagedResponse<CategoryDto>>
{
    /// <summary>Handles search categories requests and returns the application result.</summary>
    public async Task<PagedResponse<CategoryDto>> HandleAsync(SearchCategoriesQuery query, CancellationToken ct = default)
    {
        var request = query.Request;
        HandlerHelpers.EnforceTenantFilter(request, requestContext.TenantId, requestContext.Roles, logger, "CategorySearch");
        return await CqrsHandlerSupport.SearchAsync(token => repoQuery.SearchCategoriesAsync(request, token), logger, "Category", ct);
    }
}

/// <summary>Handles get category by ID work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class GetCategoryByIdHandler(
    ILogger<GetCategoryByIdHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ICategoryRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<GetCategoryByIdQuery, Result<DefaultResponse<CategoryDto>>>
{
    /// <summary>Handles get category by ID requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<CategoryDto>>> HandleAsync(GetCategoryByIdQuery query, CancellationToken ct = default)
    {
        var entity = await repoQuery.GetCategoryAsync(DomainId.From<CategoryId>(query.Id), ct);
        if (entity is null) return Result<DefaultResponse<CategoryDto>>.None();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
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
    ITenantBoundaryValidator tenantBoundaryValidator)
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
            logger, requestContext.TenantId, requestContext.Roles, dto.TenantId,
            "Category:Create", nameof(Category));
        if (boundary.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(boundary.ErrorMessage!);

        var entityResult = dto.ToEntity(dto.TenantId);
        if (entityResult.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(entityResult.ErrorMessage!);

        var entity = entityResult.Value!;
        repoTrxn.Create(ref entity);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error creating Category", ct);
        if (save.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(save.ErrorMessage!);

        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles update category work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class UpdateCategoryHandler(
    ILogger<UpdateCategoryHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ICategoryRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<UpdateCategoryCommand, Result<DefaultResponse<CategoryDto>>>
{
    /// <summary>Handles update category requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<CategoryDto>>> HandleAsync(UpdateCategoryCommand command, CancellationToken ct = default)
    {
        var dto = command.Request.Item;
        dto.TenantId = requestContext.TenantId ?? Guid.Empty;

        var validation = CategoryStructureValidator.ValidateUpdate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(validation.Errors);

        var entity = await repoTrxn.GetCategoryAsync(DomainId.From<CategoryId>(dto.Id!.Value), ct);
        if (entity is null)
        {
            return HandlerHelpers.NotFoundResponse<CategoryDto>();
        }

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Category:Update", nameof(Category), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(boundary.ErrorMessage!);

        var tenantChangeCheck = tenantBoundaryValidator.PreventTenantChange(
            logger, entity.TenantId.Value, dto.TenantId, nameof(Category), entity.Id.Value);
        if (tenantChangeCheck.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(tenantChangeCheck.ErrorMessage!);

        var updateResult = entity.Update(
            dto.Name, dto.Description, dto.SortOrder, dto.IsActive,
            DomainId.FromNullable<CategoryId>(dto.ParentCategoryId));
        if (updateResult.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(updateResult.ErrorMessage!);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error updating Category {Id}", ct, dto.Id);
        if (save.IsFailure) return Result<DefaultResponse<CategoryDto>>.Failure(save.ErrorMessage!);

        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles delete category work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class DeleteCategoryHandler(
    ILogger<DeleteCategoryHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ICategoryRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator,
    IEntityCacheProvider cache)
    : IRequestHandler<DeleteCategoryCommand, Result>
{
    /// <summary>Handles delete category requests and returns the application result.</summary>
    public async Task<Result> HandleAsync(DeleteCategoryCommand command, CancellationToken ct = default)
    {
        var entity = await repoTrxn.GetCategoryAsync(DomainId.From<CategoryId>(command.Id), ct);
        if (entity is null) return Result.Success();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Category:Delete", nameof(Category), entity.Id.Value);
        if (boundary.IsFailure) return Result.Failure(boundary.ErrorMessage!);

        // Composite FK (TenantId, CategoryId) cannot cascade to SetNull; detach the tenant's tasks first (D-022).
        await repoTrxn.ClearCategoryFromTaskItemsAsync(entity.Id, ct);
        repoTrxn.Delete(entity);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error deleting Category {Id}", ct, command.Id);
        if (save.IsFailure) return save;

        await cache.RemoveAsync(HandlerHelpers.CacheKey(nameof(Category), command.Id), ct);
        return Result.Success();
    }
}
