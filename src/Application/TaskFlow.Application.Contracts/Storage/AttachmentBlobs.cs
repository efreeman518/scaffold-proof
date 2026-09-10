namespace TaskFlow.Application.Contracts.Storage;

/// <summary>
/// Container and object naming shared by the attachment write path and the deferred blob-delete work the
/// stale-task job stages. Both must agree on the name or a delete silently targets nothing.
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

    /// <summary>Object name for one attachment: tenant, owner, file name.</summary>
    public static string BlobName(Guid tenantId, Guid ownerId, string fileName) =>
        $"{tenantId}/{ownerId}/{fileName}";
}
