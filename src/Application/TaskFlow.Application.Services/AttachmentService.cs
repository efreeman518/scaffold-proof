using EF.Tenancy;
using EF.Domain.Contracts;
using EF.Common.Contracts;
using EF.Data.Contracts;
using EF.Storage.Contracts;
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
    // No cache dependency: no cached snapshot is built from attachments, so an attachment write has nothing
    // to invalidate. Add one here the day a snapshot starts counting them.
    IObjectStorageRepository? blobStorage = null) : IAttachmentService
{
    private Guid? RequestTenantId => requestContext.TenantId;
    private IReadOnlyCollection<string> RequestRoles => requestContext.Roles;

    #region Helpers

    /// <summary>Builds response from current configuration and inputs.</summary>
    private static DefaultResponse<AttachmentDto> BuildResponse(AttachmentDto dto) =>
        new() { Item = dto, TenantInfo = null };

    #endregion

    /// <summary>Searches search and returns filtered results for callers.</summary>
    public async Task<PagedResponse<AttachmentDto>> SearchAsync(
        SearchRequest<AttachmentSearchFilter> request, bool includeTotal = false, CancellationToken ct = default)
    {
        request.Filter = tenantBoundaryValidator.EnforceTenantFilter(request.Filter, RequestTenantId, RequestRoles, "AttachmentSearch");
        return await repoQuery.SearchAttachmentsAsync(request, includeTotal, ct);
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Result<DefaultResponse<AttachmentDto>>> GetAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await repoQuery.GetAttachmentAsync(AttachmentId.From(id), ct);
        if (entity == null) return Result<DefaultResponse<AttachmentDto>>.None();

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            RequestTenantId, RequestRoles, entity.TenantId.Value,
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
            RequestTenantId, RequestRoles, dto.TenantId,
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

        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.AttachmentCreateFailed(ex);

            // D-033: a concurrent create with the same id passed the existence check too and won the insert.
            // Re-read on the query context (this one still tracks the failed insert): the winner makes this a
            // replay or a 409. Absent (or not yet replicated) means the save failed for another reason.
            if (dto.Id is Guid racedId && racedId != Guid.Empty
                && await repoQuery.GetAttachmentAsync(AttachmentId.From(racedId), ct) is { } raced)
            {
                return Result<DefaultResponse<AttachmentDto>>.Success(IdempotentCreateGuard.ReplayOrThrow(
                    raced.ToDto(), dto, IdempotentCreateGuard.IsEquivalent, nameof(Attachment), racedId));
            }

            return Result<DefaultResponse<AttachmentDto>>.Failure(ErrorConstants.ERROR_SAVE_FAILED);
        }

        return Result<DefaultResponse<AttachmentDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>Uploads upload to the configured storage backend and returns metadata.</summary>
    public async Task<Result<DefaultResponse<AttachmentDto>>> UploadAsync(
        Stream fileStream, string fileName, string contentType, long fileSizeBytes,
        AttachmentOwnerType ownerType, Guid ownerId, Guid? id = null, CancellationToken ct = default)
    {
        // GR-17: the upload form carries its own optional caller id, so it needs the same UUIDv7
        // check as the JSON create path - it was missing here, which let Guid.Empty and v4 ids through.
        var idCheck = UuidV7.ValidateCallerId(id);
        if (idCheck.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(idCheck.ErrorMessage!);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            RequestTenantId, RequestRoles, RequestTenantId,
            "Attachment:Upload", nameof(Attachment));
        if (boundary.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(boundary.ErrorMessage!);

        if (Domain.Model.Attachment.FileNameError(fileName) is { } fileNameError)
            return Result<DefaultResponse<AttachmentDto>>.Failure(fileNameError);

        if (blobStorage is null)
            return Result<DefaultResponse<AttachmentDto>>.Failure("Blob storage is not configured.");

        var tenantId = RequestTenantId ?? Guid.Empty;
        var blobName = AttachmentBlobs.NewObjectKey(tenantId, ownerId, fileName);

        try
        {
            await blobStorage.UploadAsync(AttachmentBlobs.ContainerName, blobName, fileStream, contentType, cancellationToken: ct);
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.AttachmentBlobUploadFailed(ex, fileName);
            return Result<DefaultResponse<AttachmentDto>>.Failure(ErrorConstants.ERROR_BLOB_UPLOAD_FAILED);
        }

        var storageUri = (await blobStorage.GetPresignedUrlAsync(
            AttachmentBlobs.ContainerName, blobName, AttachmentBlobs.DownloadUrlLifetime, cancellationToken: ct)).ToString();
        var entityResult = Domain.Model.Attachment.Create(
            TenantId.From(tenantId), fileName, contentType, fileSizeBytes, storageUri, ownerType, ownerId,
            DomainId.FromNullable<AttachmentId>(id), blobName);
        if (entityResult.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(entityResult.ErrorMessage!);

        var entity = entityResult.Value!;
        repoTrxn.Create(ref entity);

        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.AttachmentPersistAfterUploadFailed(ex);
            return Result<DefaultResponse<AttachmentDto>>.Failure(ErrorConstants.ERROR_SAVE_FAILED);
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

        // If-Match: * re-reads and applies again when it loses a race (D-073); a concrete version keeps its 412.
        return await ConcurrencyRetry.RunAsync(repoTrxn, expectedVersion, nameof(Attachment), dto.Id!.Value,
            attemptCt => UpdateOnceAsync(dto, expectedVersion, attemptCt), ct);
    }

    /// <summary>One read, update and save of <see cref="UpdateAsync"/>; run again on a lost wildcard race.</summary>
    private async Task<Result<DefaultResponse<AttachmentDto>>> UpdateOnceAsync(AttachmentDto dto, long? expectedVersion, CancellationToken ct)
    {
        var entity = await repoTrxn.GetAttachmentAsync(AttachmentId.From(dto.Id!.Value), ct);
        if (entity == null)
            return Result<DefaultResponse<AttachmentDto>>.Success(new DefaultResponse<AttachmentDto> { Item = null });

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            RequestTenantId, RequestRoles, entity.TenantId.Value,
            "Attachment:Update", nameof(Attachment), entity.Id.Value);
        if (boundary.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(boundary.ErrorMessage!);

        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(Attachment), entity.Id.Value);

        var tenantChangeCheck = tenantBoundaryValidator.PreventTenantChange(
            entity.TenantId.Value, dto.TenantId, nameof(Attachment), entity.Id.Value);
        if (tenantChangeCheck.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(tenantChangeCheck.ErrorMessage!);

        var updateResult = entity.Update(dto.FileName, dto.ContentType, dto.FileSizeBytes, dto.StorageUri);
        if (updateResult.IsFailure) return Result<DefaultResponse<AttachmentDto>>.Failure(updateResult.ErrorMessage!);

        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.AttachmentUpdateFailed(ex, dto.Id);
            return Result<DefaultResponse<AttachmentDto>>.Failure(ErrorConstants.ERROR_SAVE_FAILED);
        }

        return Result<DefaultResponse<AttachmentDto>>.Success(BuildResponse(entity.ToDto()));
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task<Result> DeleteAsync(Guid id, long? expectedVersion, CancellationToken ct = default)
    {
        // If-Match: * re-reads and deletes again when it loses a race (D-073); a concrete version keeps its 412. The
        // blob delete is an outside effect, so it runs once, after the save that removed the row.
        Attachment? sent = null;
        var (result, entity) = await ConcurrencyRetry.RunAsync(repoTrxn, expectedVersion, nameof(Attachment), id,
            attemptCt => DeleteOnceAsync(id, expectedVersion, sent, e => sent = e, attemptCt), ct);
        if (entity is null) return result;

        // Delete the content the upload wrote, by its stored key; a metadata-only attachment has none.
        if (blobStorage is not null && entity.StorageKey is { } storageKey)
        {
            try
            {
                await blobStorage.DeleteAsync(AttachmentBlobs.ContainerName, storageKey, ct);
            }
            catch (Exception ex)
            {
                logger.AttachmentBlobDeleteFailed(ex, id);
            }
        }

        return Result.Success();
    }

    /// <summary>One read, delete and save of <see cref="DeleteAsync"/>; <c>Deleted</c> is the removed row, if any.</summary>
    private async Task<(Result Result, Attachment? Deleted)> DeleteOnceAsync(
        Guid id, long? expectedVersion, Attachment? sent, Action<Attachment> markSaveSent, CancellationToken ct)
    {
        var entity = await repoTrxn.GetAttachmentAsync(AttachmentId.From(id), ct);
        // D-073: gone on a wildcard retry after an earlier attempt sent its save (a commit that landed but was reported
        // failed, or a competing delete) returns that attempt's row, so the blob delete still runs; gone on the first
        // attempt returns none.
        if (entity == null) return (Result.Success(), sent);

        var boundary = tenantBoundaryValidator.EnsureTenantBoundary(
            RequestTenantId, RequestRoles, entity.TenantId.Value,
            "Attachment:Delete", nameof(Attachment), entity.Id.Value);
        if (boundary.IsFailure) return (Result.Failure(boundary.ErrorMessage!), null);

        ConcurrencyGuard.Require(expectedVersion, entity.Version, nameof(Attachment), entity.Id.Value);

        repoTrxn.Delete(entity);
        markSaveSent(entity);

        try
        {
            await repoTrxn.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct);
        }
        catch (Exception ex) when (SaveFailure.MapsToFailureResult(ex))
        {
            logger.AttachmentDeleteFailed(ex, id);
            return (Result.Failure(ErrorConstants.ERROR_SAVE_FAILED), null);
        }

        return (Result.Success(), entity);
    }
}
