using Azure.Storage.Blobs;
using EF.Storage;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TaskFlow.Infrastructure.Storage;

/// <summary>
/// Azure Blob Storage arm of <c>EF.Storage.Contracts.IObjectStorageRepository</c> (D-037).
/// <see cref="BlobRepositoryBase"/> implements every member of that contract over Blob Storage, so this
/// type only binds <see cref="BlobStorageSettings"/> onto the base: the container is the
/// <c>containerName</c> argument each call carries, and containers are provisioned once by the
/// EnsureExternalResources startup task rather than checked per call.
/// </summary>
public class BlobStorageRepository(
    ILogger<BlobStorageRepository> logger,
    IOptions<BlobStorageSettings> settings,
    IAzureClientFactory<BlobServiceClient> clientFactory)
    : BlobRepositoryBase(logger, Options.Create((BlobRepositorySettingsBase)settings.Value), clientFactory);
