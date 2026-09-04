using EF.Common.Contracts;
using EF.Data.Contracts;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Application.Services.Rules;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;

namespace TaskFlow.Application.Services;

/// <summary>Coordinates attachment application use cases with validation, tenant checks, repositories, and response shaping.</summary>
internal class AttachmentService(
    ILogger<AttachmentService> logger,
    IRequestContext<string, Guid?> requestContext,
    IAttachmentRepositoryTrxn repoTrxn,
    IAttachmentRepositoryQuery repoQuery,
    ITenantBoundaryValidator tenantBoundaryValidator,
    IEntityCacheProvider cache,
    IBlobStorageRepository? blobStorage = null) : IAttachmentService
{
    private Guid? RequestTenantId => requestContext.TenantId;
    private IReadOnlyCollection<string> RequestRoles => requestContext.Roles;
    private bool IsGlobalAdmin => RequestRoles.Contains(AppConstants.ROLE_GLOBAL_ADMIN);

    #region Helpers

    /// <summary>Builds response from current configuration and inputs.</summary>
    private static DefaultResponse<AttachmentDto> BuildResponse(AttachmentDto dto) =>
        new() { Item = dto, TenantInfo = null };

    #endregion

    /// <summary>Searches search and returns filtered results for callers.</summary>
    public async Task<PagedResponse<AttachmentDto>> SearchAsync(
        SearchRequest<AttachmentSearchFilter> request, bool includeTotal = false, CancellationToken ct = default)
    {
        if (!IsGlobalAdmin)
        {
            request.Filter ??= new();
            if (request.Filter.TenantId is Guid supplied && supplied != RequestTenantId)
            {
                logger.LogTenantFilterManipulation("AttachmentSearch", RequestTenantId, supplied);
            }
            request.Filter.TenantId = RequestTenantId;
        }
        return await repoQuery.SearchAttachmentsAsync(request, includeTotal, ct);
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Result<DefaultResponse<AttachmentDto>>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await repoQuery.GetAttachmentAsync(DomainId.From<AttachmentId>(id), ct);
        if (entity == null) return Result<DefaultResponse<AttachmentDto>>.None();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, entity.TenantId.Value,
            "Attachment:Get", nameof(Attachment), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(boundary.ErrorMessage!);

        return Result<DefaultResponse<AttachmentDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>Creates requested data after validation and maps the result to the caller contract.</summary>
    public async Task<Result<DefaultResponse<AttachmentDto>>> CreateAsync(
        DefaultRequest<AttachmentDto> request, CancellationToken ct = default)
    {
        var dto = request.Item;
        dto.TenantId = RequestTenantId ?? Guid.Empty;

        var validation = AttachmentStructureValidator.ValidateCreate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(validation.Errors);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, dto.TenantId,
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

        try
        {
            await ConcurrencyGuard.SaveAsync(repoTrxn, ct);
        }
        catch (Exception ex) when (!ConcurrencyGuard.IsConcurrencyFailure(ex))
        {
            logger.LogError(ex, "Error creating Attachment");
            return Result<DefaultResponse<AttachmentDto>>.Failure(ex.GetBaseException().Message);
        }

        return Result<DefaultResponse<AttachmentDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>Uploads upload to the configured storage backend and returns metadata.</summary>
    public async Task<Result<DefaultResponse<AttachmentDto>>> UploadAsync(
        Stream fileStream, string fileName, string contentType, long fileSizeBytes,
        AttachmentOwnerType ownerType, Guid ownerId, Guid? id = null, CancellationToken ct = default)
    {
        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, RequestTenantId,
            "Attachment:Upload", nameof(Attachment));
        if (boundary.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(boundary.ErrorMessage!);

        if (blobStorage is null)
            return Result<DefaultResponse<AttachmentDto>>.Failure("Blob storage is not configured.");

        var tenantId = RequestTenantId ?? Guid.Empty;
        var blobName = $"{tenantId}/{ownerId}/{fileName}";

        try
        {
            await blobStorage.UploadAsync("attachments", blobName, fileStream, contentType, ct: ct);
        }
        catch (Exception ex) when (!ConcurrencyGuard.IsConcurrencyFailure(ex))
        {
            logger.LogError(ex, "Error uploading blob for Attachment {FileName}", fileName);
            return Result<DefaultResponse<AttachmentDto>>.Failure($"Blob upload failed: {ex.GetBaseException().Message}");
        }

        var storageUri = (await blobStorage.GetBlobUriAsync("attachments", blobName, ct)).ToString();
        var entityResult = Domain.Model.Attachment.Create(
            DomainId.From<TenantId>(tenantId), fileName, contentType, fileSizeBytes, storageUri, ownerType, ownerId,
            DomainId.FromNullable<AttachmentId>(id));
        if (entityResult.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(entityResult.ErrorMessage!);

        var entity = entityResult.Value!;
        repoTrxn.Create(ref entity);

        try
        {
            await ConcurrencyGuard.SaveAsync(repoTrxn, ct);
        }
        catch (Exception ex) when (!ConcurrencyGuard.IsConcurrencyFailure(ex))
        {
            logger.LogError(ex, "Error persisting Attachment after upload");
            return Result<DefaultResponse<AttachmentDto>>.Failure(ex.GetBaseException().Message);
        }

        return Result<DefaultResponse<AttachmentDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>Updates existing data after validation and preserves domain invariants.</summary>
    public async Task<Result<DefaultResponse<AttachmentDto>>> UpdateAsync(
        DefaultRequest<AttachmentDto> request, long? expectedVersion, CancellationToken ct = default)
    {
        var dto = request.Item;
        dto.TenantId = RequestTenantId ?? Guid.Empty;

        var validation = AttachmentStructureValidator.ValidateUpdate(dto);
        if (validation.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(validation.Errors);

        var entity = await repoTrxn.GetAttachmentAsync(DomainId.From<AttachmentId>(dto.Id!.Value), ct);
        if (entity == null)
            return Result<DefaultResponse<AttachmentDto>>.Success(new DefaultResponse<AttachmentDto> { Item = null });

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, entity.TenantId.Value,
            "Attachment:Update", nameof(Attachment), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(Attachment), entity.Id.Value);

        var tenantChangeCheck = tenantBoundaryValidator.PreventTenantChange(
            logger, entity.TenantId.Value, dto.TenantId, nameof(Attachment), entity.Id.Value);
        if (tenantChangeCheck.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(tenantChangeCheck.ErrorMessage!);

        var updateResult = entity.Update(dto.FileName, dto.ContentType, dto.FileSizeBytes, dto.StorageUri);
        if (updateResult.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(updateResult.ErrorMessage!);

        try
        {
            await ConcurrencyGuard.SaveAsync(repoTrxn, ct);
        }
        catch (Exception ex) when (!ConcurrencyGuard.IsConcurrencyFailure(ex))
        {
            logger.LogError(ex, "Error updating Attachment {Id}", dto.Id);
            return Result<DefaultResponse<AttachmentDto>>.Failure(ex.GetBaseException().Message);
        }

        return Result<DefaultResponse<AttachmentDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task<Result> DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default)
    {
        var entity = await repoTrxn.GetAttachmentAsync(DomainId.From<AttachmentId>(id), ct);
        if (entity == null) return Result.Success();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            logger, RequestTenantId, RequestRoles, entity.TenantId.Value,
            "Attachment:Delete", nameof(Attachment), entity.Id.Value);
        if (boundary.IsFailure) return Result.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(Attachment), entity.Id.Value);

        repoTrxn.Delete(entity);

        try
        {
            await ConcurrencyGuard.SaveAsync(repoTrxn, ct);
        }
        catch (Exception ex) when (!ConcurrencyGuard.IsConcurrencyFailure(ex))
        {
            logger.LogError(ex, "Error deleting Attachment {Id}", id);
            return Result.Failure(ex.GetBaseException().Message);
        }

        // Delete blob from storage if available
        if (blobStorage is not null && !string.IsNullOrEmpty(entity.StorageUri))
        {
            try
            {
                var blobName = $"{entity.TenantId.Value}/{entity.OwnerId}/{entity.FileName}";
                await blobStorage.DeleteAsync("attachments", blobName, ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to delete blob for Attachment {Id}", id);
            }
        }

        await cache.RemoveAsync($"Attachment:{id}", ct);
        return Result.Success();
    }
}
