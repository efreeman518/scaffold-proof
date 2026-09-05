using Azure.Storage.Blobs;
using EF.Storage;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskFlow.Application.Contracts.Storage;

namespace TaskFlow.Infrastructure.Storage;

/// <summary>Persists and queries blob storage data through infrastructure storage contracts.</summary>
public class BlobStorageRepository(
    ILogger<BlobStorageRepository> logger,
    IOptions<BlobStorageSettings> settings,
    IAzureClientFactory<BlobServiceClient> clientFactory)
    : BlobRepositoryBase(logger, Options.Create((BlobRepositorySettingsBase)settings.Value), clientFactory),
      IBlobStorageRepository
{
    // Containers are provisioned once by the EnsureExternalResources startup task, never per call: an
    // exists-check on every upload, download, and delete is a round trip on the hot path guarding a
    // condition that can only be true once.
    private readonly ContainerInfo _container = new()
    {
        ContainerName = settings.Value.ContainerName,
        CreateContainerIfNotExist = false
    };

    /// <summary>Uploads upload to the configured storage backend and returns metadata.</summary>
    public Task UploadAsync(string containerName, string blobName, Stream content,
        string? contentType = null, IDictionary<string, string>? metadata = null,
        CancellationToken ct = default) =>
        UploadBlobStreamAsync(new ContainerInfo { ContainerName = containerName, CreateContainerIfNotExist = false },
            blobName, content, contentType, false, metadata, ct);

    /// <summary>Downloads download from the configured storage backend.</summary>
    public async Task<Stream> DownloadAsync(string containerName, string blobName,
        CancellationToken ct = default) =>
        await StartDownloadBlobStreamAsync(new ContainerInfo { ContainerName = containerName }, blobName, false, ct);

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public Task DeleteAsync(string containerName, string blobName,
        CancellationToken ct = default) =>
        DeleteBlobAsync(new ContainerInfo { ContainerName = containerName }, blobName, ct);

    /// <summary>Checks whether exists exists in the configured backend.</summary>
    public async Task<bool> ExistsAsync(string containerName, string blobName,
        CancellationToken ct = default)
    {
        var info = new ContainerInfo { ContainerName = containerName, CreateContainerIfNotExist = false };
        var (blobs, _) = await QueryPageBlobsAsync(info, prefix: blobName, cancellationToken: ct);
        return blobs.Any(b => b.Name == blobName);
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Uri> GetBlobUriAsync(string containerName, string blobName,
        CancellationToken ct = default)
    {
        var sasUri = await GenerateBlobSasUriAsync(
            new ContainerInfo { ContainerName = containerName },
            blobName,
            Azure.Storage.Sas.BlobSasPermissions.Read,
            DateTimeOffset.UtcNow.AddHours(1),
            cancellationToken: ct);
        return sasUri ?? throw new InvalidOperationException($"Could not generate URI for blob {containerName}/{blobName}");
    }
}
