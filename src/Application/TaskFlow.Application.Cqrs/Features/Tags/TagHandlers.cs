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
    ILogger<SearchTagsHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ITagRepositoryQuery repoQuery)
    : IRequestHandler<SearchTagsQuery, PagedResponse<TagDto>>
{
    /// <summary>Handles search tags requests and returns the application result.</summary>
    public async Task<PagedResponse<TagDto>> HandleAsync(SearchTagsQuery query, CancellationToken ct = default)
    {
        var request = query.Request;
        HandlerHelpers.EnforceTenantFilter(request, requestContext.TenantId, requestContext.Roles, logger, "TagSearch");
        return await CqrsHandlerSupport.SearchAsync(token => repoQuery.SearchTagsAsync(request, query.IncludeTotal, token), logger, "Tag", ct);
    }
}

/// <summary>Handles get tag by ID work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class GetTagByIdHandler(
    ILogger<GetTagByIdHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    ITagRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<GetTagByIdQuery, Result<DefaultResponse<TagDto>>>
{
    /// <summary>Handles get tag by ID requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<TagDto>>> HandleAsync(GetTagByIdQuery query, CancellationToken ct = default)
    {
        var entity = await repoQuery.GetTagAsync(DomainId.From<TagId>(query.Id), ct);
        if (entity is null) return Result<DefaultResponse<TagDto>>.None();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Tag:Get", nameof(Tag), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<TagDto>>.Failure(boundary.ErrorMessage!);

        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles create tag work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class CreateTagHandler(
    ILogger<CreateTagHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    IRepositoryTrxn<Tag, TagId> repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator,
    ITaskFlowCache cache)
    : IRequestHandler<CreateTagCommand, Result<DefaultResponse<TagDto>>>
{
    /// <summary>Handles create tag requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<TagDto>>> HandleAsync(CreateTagCommand command, CancellationToken ct = default)
    {
        var dto = command.Request.Item;
        dto.TenantId = requestContext.TenantId ?? Guid.Empty;

        var validation = TagStructureValidator.ValidateCreate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<TagDto>>.Failure(validation.Errors);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, requestContext.TenantId, requestContext.Roles, dto.TenantId,
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

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error creating Tag", ct);
        if (save.IsFailure) return Result<DefaultResponse<TagDto>>.Failure(save.ErrorMessage!);

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
    ITaskFlowCache cache)
    : IRequestHandler<UpdateTagCommand, Result<DefaultResponse<TagDto>>>
{
    /// <summary>Handles update tag requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<TagDto>>> HandleAsync(UpdateTagCommand command, CancellationToken ct = default)
    {
        var dto = command.Request.Item;
        dto.TenantId = requestContext.TenantId ?? Guid.Empty;

        var validation = TagStructureValidator.ValidateUpdate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<TagDto>>.Failure(validation.Errors);

        var entity = await repoTrxn.GetAsync(DomainId.From<TagId>(dto.Id!.Value), ct);
        if (entity is null)
        {
            return HandlerHelpers.NotFoundResponse<TagDto>();
        }

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Tag:Update", nameof(Tag), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<TagDto>>.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(Tag), entity.Id.Value);

        var tenantChangeCheck = tenantBoundaryValidator.PreventTenantChange(
            logger, entity.TenantId.Value, dto.TenantId, nameof(Tag), entity.Id.Value);
        if (tenantChangeCheck.IsFailure) return Result<DefaultResponse<TagDto>>.Failure(tenantChangeCheck.ErrorMessage!);

        var updateResult = entity.Update(dto.Name, dto.Color);
        if (updateResult.IsFailure) return Result<DefaultResponse<TagDto>>.Failure(updateResult.ErrorMessage!);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error updating Tag {Id}", ct, dto.Id);
        if (save.IsFailure) return Result<DefaultResponse<TagDto>>.Failure(save.ErrorMessage!);

        await cache.RemoveByTagAsync(HandlerHelpers.EntityTag(requestContext.TenantId, nameof(Tag)), ct);
        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles delete tag work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class DeleteTagHandler(
    ILogger<DeleteTagHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    IRepositoryTrxn<Tag, TagId> repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator,
    ITaskFlowCache cache)
    : IRequestHandler<DeleteTagCommand, Result>
{
    /// <summary>Handles delete tag requests and returns the application result.</summary>
    public async Task<Result> HandleAsync(DeleteTagCommand command, CancellationToken ct = default)
    {
        var entity = await repoTrxn.GetAsync(DomainId.From<TagId>(command.Id), ct);
        if (entity is null) return Result.Success();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Tag:Delete", nameof(Tag), entity.Id.Value);
        if (boundary.IsFailure) return Result.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(Tag), entity.Id.Value);

        repoTrxn.Delete(entity);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error deleting Tag {Id}", ct, command.Id);
        if (save.IsFailure) return save;

        await cache.RemoveByTagAsync(HandlerHelpers.EntityTag(requestContext.TenantId, nameof(Tag)), ct);
        return Result.Success();
    }
}
