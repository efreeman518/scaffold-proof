using EF.Common;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Domain.Shared.Constants;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Repositories;

/// <summary>
/// The <see cref="BlobDeleteWork"/> rows for attachment content (D-026), built in one place for every path that stages
/// them: the attachment delete, the stale-task cleanup and the upload reservation.
/// </summary>
internal static class AttachmentBlobDeleteWork
{
    /// <summary>
    /// Deletes the content of one attachment. The id is a UUIDv5 of the tenant and the attachment id, so a retried
    /// cleanup batch, or the attachment delete and the stale-task cleanup staging the same attachment, share one work
    /// row instead of queueing the blob twice.
    /// </summary>
    public static BlobDeleteWork ForAttachment(Guid tenantId, Guid attachmentId, string storageKey, DateTimeOffset availableAtUtc) =>
        Create(
            DeterministicGuid.Create(DomainConstants.DETERMINISTIC_ID_NAMESPACE, "blob-delete", tenantId.ToString(), attachmentId.ToString()),
            tenantId, storageKey, availableAtUtc);

    /// <summary>
    /// Reserves the delete of content an upload is about to write under a new key, claimable only from
    /// <paramref name="availableAtUtc"/>. The id is a fresh UUIDv7: its version nibble differs from the UUIDv5 of
    /// <see cref="ForAttachment"/>, so a reservation can never take the id of the delete staged for the same attachment.
    /// </summary>
    public static BlobDeleteWork UploadReservation(Guid tenantId, string storageKey, DateTimeOffset availableAtUtc) =>
        Create(Guid.CreateVersion7(), tenantId, storageKey, availableAtUtc);

    private static BlobDeleteWork Create(Guid id, Guid tenantId, string storageKey, DateTimeOffset availableAtUtc) => new()
    {
        Id = id,
        TenantId = tenantId,
        AvailableAtUtc = availableAtUtc,
        ContainerName = AttachmentBlobs.ContainerName,
        BlobName = storageKey
    };
}
