namespace TaskFlow.Application.Contracts.Storage;

/// <summary>
/// Container and object naming for attachment content. The upload path mints the key once and stores it on the
/// attachment row; the attachment delete and the stale-task job stage deferred blob-delete work for it by that key.
/// <para>
/// The storage port itself is <c>EF.Storage.Contracts.IObjectStorageRepository</c> (D-037): Azure Blob via
/// <c>EF.Storage.BlobRepositoryBase</c>, S3-compatible via <c>EF.Storage.S3</c>, and a no-op arm when no
/// backend is configured.
/// </para>
/// </summary>
public static class AttachmentBlobs
{
    /// <summary>Container attachments are written to (matches <c>BlobStorageSettings.ContainerName</c>).</summary>
    public const string ContainerName = "attachments";

    /// <summary>
    /// Lifetime of the presigned download URL handed to a client. Both arms take the lifetime per call
    /// (<c>IObjectStorageRepository.GetPresignedUrlAsync</c>), so the attachment read path states it once
    /// here instead of each backend carrying its own default.
    /// </summary>
    public static readonly TimeSpan DownloadUrlLifetime = TimeSpan.FromHours(1);

    /// <summary>
    /// A new object key for one uploaded attachment: tenant, owner, a server-generated UUIDv7, then the file name as
    /// uploaded, so a downloaded blob keeps its name and extension (D-075). The caller validates the name with
    /// <c>Attachment.FileNameError</c> first (required, at most 255 characters, no '/', '\', "..", control or format
    /// characters or line or paragraph separators, no trailing '.' or whitespace), so it stays one final segment
    /// under the UUIDv7 and cannot reach another prefix. The key is stored once on the row (<c>Attachment.StorageKey</c>)
    /// and every read and delete uses the stored key; a later rename never changes it.
    /// </summary>
    public static string NewObjectKey(Guid tenantId, Guid ownerId, string fileName) =>
        $"{tenantId}/{ownerId}/{Guid.CreateVersion7()}/{fileName}";
}
