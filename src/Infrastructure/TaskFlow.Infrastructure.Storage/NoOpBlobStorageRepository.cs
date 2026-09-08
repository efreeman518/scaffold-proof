using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts.Storage;

namespace TaskFlow.Infrastructure.Storage;

/// <summary>
/// Storage fallback for deployments without a configured object-storage backend (D-037), so
/// <see cref="IBlobStorageRepository"/> is always resolvable and the DI graph builds without one. Delete
/// is a real no-op success: a target that was never written is already gone, the same semantics
/// <c>BlobDeleteWorkerService</c> already gives a genuinely missing blob, so deferred deletes drain
/// instead of piling up. Upload, download, and URI generation throw instead - attachment content cannot
/// be fabricated, and pretending success there would be silent data loss. Logged once at construction
/// (registered as a singleton) rather than per call.
/// </summary>
public class NoOpBlobStorageRepository : IBlobStorageRepository
{
    private const string NotConfiguredMessage =
        "No object-storage backend is configured (Storage:Provider / TASKFLOW_STORAGE_PROVIDER).";

    public NoOpBlobStorageRepository(ILogger<NoOpBlobStorageRepository> logger) =>
        logger.NoOpBlobStorageConfigured();

    /// <summary>Attachment content cannot be fabricated, so a real upload attempt is a hard failure.</summary>
    public Task UploadAsync(string containerName, string blobName, Stream content,
        string? contentType = null, IDictionary<string, string>? metadata = null,
        CancellationToken ct = default) =>
        throw new InvalidOperationException($"{NotConfiguredMessage} Attachment upload is unavailable.");

    /// <summary>Nothing was ever stored, so there is nothing to download.</summary>
    public Task<Stream> DownloadAsync(string containerName, string blobName, CancellationToken ct = default) =>
        throw new InvalidOperationException($"{NotConfiguredMessage} Attachment download is unavailable.");

    /// <summary>Nothing was ever stored, so the target is already gone - counts as success.</summary>
    public Task DeleteAsync(string containerName, string blobName, CancellationToken ct = default) =>
        Task.CompletedTask;

    /// <summary>Nothing was ever stored.</summary>
    public Task<bool> ExistsAsync(string containerName, string blobName, CancellationToken ct = default) =>
        Task.FromResult(false);

    /// <summary>There is no backend to generate a URI against.</summary>
    public Task<Uri> GetBlobUriAsync(string containerName, string blobName, CancellationToken ct = default) =>
        throw new InvalidOperationException($"{NotConfiguredMessage} Attachment URL is unavailable.");
}
