using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Application.Cqrs.Shared;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

namespace TaskFlow.Application.Cqrs.Features.Attachments;

/// <summary>Handles search attachments work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class SearchAttachmentsHandler(
    ILogger<SearchAttachmentsHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    IAttachmentRepositoryQuery repoQuery)
    : IRequestHandler<SearchAttachmentsQuery, PagedResponse<AttachmentDto>>
{
    /// <summary>Handles search attachments requests and returns the application result.</summary>
    public async Task<PagedResponse<AttachmentDto>> HandleAsync(SearchAttachmentsQuery query, CancellationToken ct = default)
    {
        var request = query.Request;
        HandlerHelpers.EnforceTenantFilter(request, requestContext.TenantId, requestContext.Roles, logger, "AttachmentSearch");
        return await CqrsHandlerSupport.SearchAsync(token => repoQuery.SearchAttachmentsAsync(request, query.IncludeTotal, token), logger, "Attachment", ct);
    }
}

/// <summary>Handles get attachment by ID work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class GetAttachmentByIdHandler(
    ILogger<GetAttachmentByIdHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    IAttachmentRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<GetAttachmentByIdQuery, Result<DefaultResponse<AttachmentDto>>>
{
    /// <summary>Handles get attachment by ID requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<AttachmentDto>>> HandleAsync(GetAttachmentByIdQuery query, CancellationToken ct = default)
    {
        var entity = await repoQuery.GetAttachmentAsync(DomainId.From<AttachmentId>(query.Id), ct);
        if (entity is null) return Result<DefaultResponse<AttachmentDto>>.None();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Attachment:Get", nameof(Attachment), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(boundary.ErrorMessage!);

        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles create attachment work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class CreateAttachmentHandler(
    ILogger<CreateAttachmentHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    IAttachmentRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<CreateAttachmentCommand, Result<DefaultResponse<AttachmentDto>>>
{
    /// <summary>Handles create attachment requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<AttachmentDto>>> HandleAsync(CreateAttachmentCommand command, CancellationToken ct = default)
    {
        var dto = command.Request.Item;
        dto.TenantId = requestContext.TenantId ?? Guid.Empty;

        var validation = AttachmentStructureValidator.ValidateCreate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(validation.Errors);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, requestContext.TenantId, requestContext.Roles, dto.TenantId,
            "Attachment:Create", nameof(Attachment));
        if (boundary.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(boundary.ErrorMessage!);

        // D-033: the row itself is the idempotency record for a caller-supplied UUIDv7 id.
        if (dto.Id is Guid callerId && callerId != Guid.Empty)
        {
            var existing = await repoTrxn.GetAttachmentAsync(DomainId.From<AttachmentId>(callerId), ct);
            if (existing is not null)
            {
                var existingDto = existing.ToDto();
                if (!IdempotentCreateGuard.IsEquivalent(existingDto, dto))
                    throw new IdempotentCreateConflictException(nameof(Attachment), callerId);

                return Result<DefaultResponse<AttachmentDto>>.Success(
                    new DefaultResponse<AttachmentDto> { Item = existingDto, IsReplay = true });
            }
        }

        var entityResult = dto.ToEntity(dto.TenantId);
        if (entityResult.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(entityResult.ErrorMessage!);

        var entity = entityResult.Value!;
        repoTrxn.Create(ref entity);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error creating Attachment", ct);
        if (save.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(save.ErrorMessage!);

        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles upload attachment work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class UploadAttachmentHandler(
    ILogger<UploadAttachmentHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    IAttachmentRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator,
    IBlobStorageRepository? blobStorage = null)
    : IRequestHandler<UploadAttachmentCommand, Result<DefaultResponse<AttachmentDto>>>
{
    /// <summary>Handles upload attachment requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<AttachmentDto>>> HandleAsync(UploadAttachmentCommand command, CancellationToken ct = default)
    {
        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, requestContext.TenantId, requestContext.Roles, requestContext.TenantId,
            "Attachment:Upload", nameof(Attachment));
        if (boundary.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(boundary.ErrorMessage!);

        if (blobStorage is null)
            return Result<DefaultResponse<AttachmentDto>>.Failure("Blob storage is not configured.");

        var tenantId = requestContext.TenantId ?? Guid.Empty;
        var blobName = $"{tenantId}/{command.OwnerId}/{command.FileName}";

        try
        {
            await blobStorage.UploadAsync("attachments", blobName, command.FileStream, command.ContentType, ct: ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Error uploading blob for Attachment {FileName}", command.FileName);
            return Result<DefaultResponse<AttachmentDto>>.Failure($"Blob upload failed: {ex.GetBaseException().Message}");
        }

        var storageUri = (await blobStorage.GetBlobUriAsync("attachments", blobName, ct)).ToString();
        var entityResult = Attachment.Create(
            DomainId.From<TenantId>(tenantId),
            command.FileName,
            command.ContentType,
            command.FileSizeBytes,
            storageUri,
            command.OwnerType,
            command.OwnerId,
            DomainId.FromNullable<AttachmentId>(command.Id));
        if (entityResult.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(entityResult.ErrorMessage!);

        var entity = entityResult.Value!;
        repoTrxn.Create(ref entity);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error persisting Attachment after upload", ct);
        if (save.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(save.ErrorMessage!);

        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles update attachment work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class UpdateAttachmentHandler(
    ILogger<UpdateAttachmentHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    IAttachmentRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<UpdateAttachmentCommand, Result<DefaultResponse<AttachmentDto>>>
{
    /// <summary>Handles update attachment requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<AttachmentDto>>> HandleAsync(UpdateAttachmentCommand command, CancellationToken ct = default)
    {
        var dto = command.Request.Item;
        dto.TenantId = requestContext.TenantId ?? Guid.Empty;

        var validation = AttachmentStructureValidator.ValidateUpdate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(validation.Errors);

        var entity = await repoTrxn.GetAttachmentAsync(DomainId.From<AttachmentId>(dto.Id!.Value), ct);
        if (entity is null)
        {
            return HandlerHelpers.NotFoundResponse<AttachmentDto>();
        }

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Attachment:Update", nameof(Attachment), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(Attachment), entity.Id.Value);

        var tenantChangeCheck = tenantBoundaryValidator.PreventTenantChange(
            logger, entity.TenantId.Value, dto.TenantId, nameof(Attachment), entity.Id.Value);
        if (tenantChangeCheck.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(tenantChangeCheck.ErrorMessage!);

        var updateResult = entity.Update(dto.FileName, dto.ContentType, dto.FileSizeBytes, dto.StorageUri);
        if (updateResult.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(updateResult.ErrorMessage!);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error updating Attachment {Id}", ct, dto.Id);
        if (save.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(save.ErrorMessage!);

        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles delete attachment work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class DeleteAttachmentHandler(
    ILogger<DeleteAttachmentHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    IAttachmentRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator,
    ITaskFlowCache cache,
    IBlobStorageRepository? blobStorage = null)
    : IRequestHandler<DeleteAttachmentCommand, Result>
{
    /// <summary>Handles delete attachment requests and returns the application result.</summary>
    public async Task<Result> HandleAsync(DeleteAttachmentCommand command, CancellationToken ct = default)
    {
        var entity = await repoTrxn.GetAttachmentAsync(DomainId.From<AttachmentId>(command.Id), ct);
        if (entity is null) return Result.Success();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Attachment:Delete", nameof(Attachment), entity.Id.Value);
        if (boundary.IsFailure) return Result.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(Attachment), entity.Id.Value);

        repoTrxn.Delete(entity);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error deleting Attachment {Id}", ct, command.Id);
        if (save.IsFailure) return save;

        if (blobStorage is not null && !string.IsNullOrEmpty(entity.StorageUri))
        {
            try
            {
                var blobName = $"{entity.TenantId.Value}/{entity.OwnerId}/{entity.FileName}";
                await blobStorage.DeleteAsync("attachments", blobName, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete blob for Attachment {Id}", command.Id);
            }
        }

        await cache.RemoveByTagAsync(HandlerHelpers.EntityTag(requestContext.TenantId, nameof(Attachment)), ct);
        return Result.Success();
    }
}
