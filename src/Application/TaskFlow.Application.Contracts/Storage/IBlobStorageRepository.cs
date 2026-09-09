namespace TaskFlow.Application.Contracts.Storage;

/// <summary>
/// Container and blob naming shared by the attachment write path and the deferred blob-delete work the
/// stale-task job stages. Both must agree on the name or a delete silently targets nothing.
/// </summary>
public static class AttachmentBlobs
{
    /// <summary>Container attachments are written to (matches <c>BlobStorageSettings.ContainerName</c>).</summary>
    public const string ContainerName = "attachments";

    /// <summary>Blob path for one attachment: tenant, owner, file name.</summary>
    public static string BlobName(Guid tenantId, Guid ownerId, string fileName) =>
        $"{tenantId}/{ownerId}/{fileName}";
}

/// <summary>Persists and queries i blob storage data through infrastructure storage contracts.</summary>
public interface IBlobStorageRepository
{
    /// <summary>Uploads upload to the configured storage backend and returns metadata.</summary>
    Task UploadAsync(string containerName, string blobName, Stream content,
        string? contentType = null, IDictionary<string, string>? metadata = null,
        CancellationToken ct = default);

    /// <summary>Downloads download from the configured storage backend.</summary>
    Task<Stream> DownloadAsync(string containerName, string blobName,
        CancellationToken ct = default);

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    Task DeleteAsync(string containerName, string blobName,
        CancellationToken ct = default);

    /// <summary>Checks whether exists exists in the configured backend.</summary>
    Task<bool> ExistsAsync(string containerName, string blobName,
        CancellationToken ct = default);

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    Task<Uri> GetBlobUriAsync(string containerName, string blobName,
        CancellationToken ct = default);
}
