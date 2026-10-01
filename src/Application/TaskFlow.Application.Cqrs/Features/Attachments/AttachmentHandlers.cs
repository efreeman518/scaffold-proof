using EF.Tenancy;
using EF.Domain.Contracts;
using EF.Data.Contracts;
using EF.Cache;
using EF.Common.Contracts;
using EF.CQRS.Abstractions;
using EF.Storage.Contracts;
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
    IRequestContext<string, Guid?> requestContext,
    IAttachmentRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<SearchAttachmentsQuery, PagedResponse<AttachmentDto>>
{
    /// <summary>Handles search attachments requests and returns the application result.</summary>
    public async Task<PagedResponse<AttachmentDto>> HandleAsync(SearchAttachmentsQuery query, CancellationToken ct = default)
    {
        var request = query.Request;
        request.Filter = tenantBoundaryValidator.EnforceTenantFilter(request.Filter, requestContext.TenantId, requestContext.Roles, "AttachmentSearch");
        return await repoQuery.SearchAttachmentsAsync(request, query.IncludeTotal, ct);
    }
}

/// <summary>Handles get attachment by ID work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class GetAttachmentByIdHandler(
    IRequestContext<string, Guid?> requestContext,
    IAttachmentRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator)
    : IRequestHandler<GetAttachmentByIdQuery, Result<DefaultResponse<AttachmentDto>>>
{
    /// <summary>Handles get attachment by ID requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<AttachmentDto>>> HandleAsync(GetAttachmentByIdQuery query, CancellationToken ct = default)
    {
        var entity = await repoQuery.GetAttachmentAsync(AttachmentId.From(query.Id), ct);
        if (entity is null) return Result<DefaultResponse<AttachmentDto>>.None();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
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
    IAttachmentRepositoryQuery repoQuery,
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
            requestContext.TenantId, requestContext.Roles, dto.TenantId,
            "Attachment:Create", nameof(Attachment));
        if (boundary.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(boundary.ErrorMessage!);

        // D-033: the row itself is the idempotency record for a caller-supplied UUIDv7 id.
        if (dto.Id is Guid callerId && callerId != Guid.Empty)
        {
            var existing = await repoTrxn.GetAttachmentAsync(AttachmentId.From(callerId), ct);
            if (existing is not null)
            {
                return Result<DefaultResponse<AttachmentDto>>.Success(IdempotentCreateGuard.ReplayOrThrow(
                    existing.ToDto(), dto, IdempotentCreateGuard.IsEquivalent, nameof(Attachment), callerId));
            }
        }

        var entityResult = dto.ToEntity(dto.TenantId);
        if (entityResult.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(entityResult.ErrorMessage!);

        var entity = entityResult.Value!;
        repoTrxn.Create(ref entity);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error creating Attachment", ct);
        if (save.IsFailure)
        {
            // D-033: a concurrent create with the same id passed the existence check too and won the insert.
            // Re-read on the query context (this one still tracks the failed insert): the winner makes this a
            // replay or a 409. Absent (or not yet replicated) means the save failed for another reason.
            if (dto.Id is Guid racedId && racedId != Guid.Empty
                && await repoQuery.GetAttachmentAsync(AttachmentId.From(racedId), ct) is { } raced)
            {
                return Result<DefaultResponse<AttachmentDto>>.Success(IdempotentCreateGuard.ReplayOrThrow(
                    raced.ToDto(), dto, IdempotentCreateGuard.IsEquivalent, nameof(Attachment), racedId));
            }

            return Result<DefaultResponse<AttachmentDto>>.Failure(save.ErrorMessage!);
        }

        return HandlerHelpers.Success(entity.ToDto());
    }
}

/// <summary>Handles upload attachment work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
internal sealed class UploadAttachmentHandler(
    ILogger<UploadAttachmentHandler> logger,
    IRequestContext<string, Guid?> requestContext,
    IAttachmentRepositoryTrxn repoTrxn,
    ITenantBoundaryValidator tenantBoundaryValidator,
    IObjectStorageRepository? blobStorage = null)
    : IRequestHandler<UploadAttachmentCommand, Result<DefaultResponse<AttachmentDto>>>
{
    /// <summary>Handles upload attachment requests and returns the application result.</summary>
    public async Task<Result<DefaultResponse<AttachmentDto>>> HandleAsync(UploadAttachmentCommand command, CancellationToken ct = default)
    {
        // GR-17: the upload form carries its own optional caller id, so it needs the same UUIDv7
        // check as the JSON create path - it was missing here, which let Guid.Empty and v4 ids through.
        var idCheck = UuidV7.ValidateCallerId(command.Id);
        if (idCheck.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(idCheck.ErrorMessage!);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, requestContext.TenantId,
            "Attachment:Upload", nameof(Attachment));
        if (boundary.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(boundary.ErrorMessage!);

        if (blobStorage is null)
            return Result<DefaultResponse<AttachmentDto>>.Failure("Blob storage is not configured.");

        var tenantId = requestContext.TenantId ?? Guid.Empty;
        var blobName = $"{tenantId}/{command.OwnerId}/{command.FileName}";

        try
        {
            await blobStorage.UploadAsync(AttachmentBlobs.ContainerName, blobName, command.FileStream, command.ContentType, cancellationToken: ct);
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.AttachmentBlobUploadFailed(ex, command.FileName);
            return Result<DefaultResponse<AttachmentDto>>.Failure(ErrorConstants.ERROR_BLOB_UPLOAD_FAILED);
        }

        var storageUri = (await blobStorage.GetPresignedUrlAsync(
            AttachmentBlobs.ContainerName, blobName, AttachmentBlobs.DownloadUrlLifetime, cancellationToken: ct)).ToString();
        var entityResult = Attachment.Create(
            TenantId.From(tenantId),
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

        // If-Match: * re-reads and applies again when it loses a race (D-073); a concrete version keeps its 412.
        return await ConcurrencyRetry.RunAsync(repoTrxn, command.ExpectedVersion, nameof(Attachment), dto.Id!.Value,
            attemptCt => UpdateOnceAsync(dto, command.ExpectedVersion, attemptCt), ct);
    }

    /// <summary>One read, update and save; run again on a lost wildcard race.</summary>
    private async Task<Result<DefaultResponse<AttachmentDto>>> UpdateOnceAsync(AttachmentDto dto, long? expectedVersion, CancellationToken ct)
    {
        var entity = await repoTrxn.GetAttachmentAsync(AttachmentId.From(dto.Id!.Value), ct);
        if (entity is null)
        {
            return HandlerHelpers.NotFoundResponse<AttachmentDto>();
        }

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Attachment:Update", nameof(Attachment), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(Attachment), entity.Id.Value);

        var tenantChangeCheck = tenantBoundaryValidator.PreventTenantChange(
            entity.TenantId.Value, dto.TenantId, nameof(Attachment), entity.Id.Value);
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
    ITypedCache cache,
    IObjectStorageRepository? blobStorage = null)
    : IRequestHandler<DeleteAttachmentCommand, Result>
{
    /// <summary>Handles delete attachment requests and returns the application result.</summary>
    public async Task<Result> HandleAsync(DeleteAttachmentCommand command, CancellationToken ct = default)
    {
        // If-Match: * re-reads and deletes again when it loses a race (D-073); a concrete version keeps its 412. The
        // blob delete and cache eviction are outside effects, so they run once, after the save that removed the row.
        var (result, entity) = await ConcurrencyRetry.RunAsync(repoTrxn, command.ExpectedVersion, nameof(Attachment), command.Id,
            attemptCt => DeleteOnceAsync(command, attemptCt), ct);
        if (entity is null) return result;

        if (blobStorage is not null && !string.IsNullOrEmpty(entity.StorageUri))
        {
            try
            {
                var blobName = AttachmentBlobs.BlobName(entity.TenantId.Value, entity.OwnerId, entity.FileName);
                await blobStorage.DeleteAsync(AttachmentBlobs.ContainerName, blobName, ct);
            }
            catch (Exception ex)
            {
                logger.AttachmentBlobDeleteFailed(ex, command.Id);
            }
        }

        await cache.RemoveByTagAsync(HandlerHelpers.EntityTag(requestContext.TenantId, nameof(Attachment)), ct);
        return Result.Success();
    }

    /// <summary>One read, delete and save; <c>Deleted</c> is the removed row, if any.</summary>
    private async Task<(Result Result, Attachment? Deleted)> DeleteOnceAsync(DeleteAttachmentCommand command, CancellationToken ct)
    {
        var entity = await repoTrxn.GetAttachmentAsync(AttachmentId.From(command.Id), ct);
        if (entity is null) return (Result.Success(), null);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            requestContext.TenantId, requestContext.Roles, entity.TenantId.Value,
            "Attachment:Delete", nameof(Attachment), entity.Id.Value);
        if (boundary.IsFailure) return (Result.Failure(boundary.ErrorMessage!), null);

        ConcurrencyGuard.Require(command.ExpectedVersion, entity.Version, nameof(Attachment), entity.Id.Value);

        repoTrxn.Delete(entity);

        var save = await CqrsHandlerSupport.TrySaveAsync(repoTrxn, logger, "Error deleting Attachment {Id}", ct, command.Id);
        return (save, save.IsSuccess ? entity : null);
    }
}
