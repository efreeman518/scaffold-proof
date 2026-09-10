using EF.Storage.Contracts;
using Microsoft.Extensions.Logging;

namespace TaskFlow.Infrastructure.Storage;

/// <summary>
/// Object-storage arm for deployments without a configured backend (D-037), so
/// <see cref="IObjectStorageRepository"/> is always resolvable and the DI graph builds without one. Delete
/// is a real no-op success: a target that was never written is already gone, the same semantics
/// <c>BlobDeleteWorkerService</c> already gives a genuinely missing blob, so deferred deletes drain
/// instead of piling up. Upload, download, and presigned-URL generation throw instead - attachment content
/// cannot be fabricated, and pretending success there would be silent data loss. Logged once at
/// construction (registered as a singleton) rather than per call.
/// </summary>
public class NoOpBlobStorageRepository : IObjectStorageRepository
{
    private const string NotConfiguredMessage =
        "No object-storage backend is configured (Storage:Provider / TASKFLOW_STORAGE_PROVIDER).";

    public NoOpBlobStorageRepository(ILogger<NoOpBlobStorageRepository> logger) =>
        logger.NoOpBlobStorageConfigured();

    /// <summary>Attachment content cannot be fabricated, so a real upload attempt is a hard failure.</summary>
    public Task UploadAsync(string containerName, string objectName, Stream content,
        string? contentType = null, IDictionary<string, string>? metadata = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException($"{NotConfiguredMessage} Attachment upload is unavailable.");

    /// <summary>Nothing was ever stored, so there is nothing to download.</summary>
    public Task<Stream> DownloadAsync(string containerName, string objectName,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException($"{NotConfiguredMessage} Attachment download is unavailable.");

    /// <summary>Nothing was ever stored, so the target is already gone - counts as success.</summary>
    public Task DeleteAsync(string containerName, string objectName, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Nothing was ever stored.</summary>
    public Task<bool> ExistsAsync(string containerName, string objectName, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>There is no backend to sign a URL against.</summary>
    public Task<Uri> GetPresignedUrlAsync(string containerName, string objectName, TimeSpan lifetime,
        ObjectStoragePermissions permissions = ObjectStoragePermissions.Read,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException($"{NotConfiguredMessage} Attachment URL is unavailable.");

    /// <summary>Nothing was ever stored, so every listing is empty.</summary>
    public Task<ObjectStoragePage> ListAsync(string containerName, string? prefix = null,
        string? continuationToken = null, int pageSize = 100, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ObjectStoragePage([], null));
}
