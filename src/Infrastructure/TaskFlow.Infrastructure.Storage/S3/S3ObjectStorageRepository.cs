using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using TaskFlow.Application.Contracts.Storage;

namespace TaskFlow.Infrastructure.Storage.S3;

/// <summary>
/// S3-compatible implementation of <see cref="IBlobStorageRepository"/> (D-037): MinIO locally/on the VPS,
/// any S3-compatible provider in production. Bucket = the <c>containerName</c> argument (the Azure arm's
/// container concept carries over unchanged); key = the existing <c>{tenantId}/{ownerId}/{fileName}</c>
/// path convention (<see cref="AttachmentBlobs"/>) - this repository applies no further transformation to it.
/// </summary>
public sealed class S3ObjectStorageRepository(
    IAmazonS3 client,
    [FromKeyedServices(S3ObjectStorageRepository.PublicClientKey)] IAmazonS3 publicClient,
    S3StorageSettings settings) : IBlobStorageRepository
{
    /// <summary>DI key for the client used only to sign presigned URLs against <see cref="S3StorageSettings.PublicServiceUrl"/>.</summary>
    public const string PublicClientKey = "s3-public";

    /// <summary>Uploads upload to the configured storage backend and returns metadata.</summary>
    public async Task UploadAsync(string containerName, string blobName, Stream content,
        string? contentType = null, IDictionary<string, string>? metadata = null,
        CancellationToken ct = default)
    {
        var request = new PutObjectRequest
        {
            BucketName = containerName,
            Key = blobName,
            InputStream = content,
            ContentType = contentType,
            // The caller owns the stream's lifetime (matches the Azure arm's UploadBlobStreamAsync contract).
            AutoCloseStream = false
        };

        if (metadata is not null)
        {
            foreach (var (key, value) in metadata)
                request.Metadata.Add(key, value);
        }

        await client.PutObjectAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>Downloads download from the configured storage backend.</summary>
    public async Task<Stream> DownloadAsync(string containerName, string blobName,
        CancellationToken ct = default)
    {
        var response = await client
            .GetObjectAsync(new GetObjectRequest { BucketName = containerName, Key = blobName }, ct)
            .ConfigureAwait(false);
        return response.ResponseStream;
    }

    /// <summary>Deletes requested data and maps failures to the caller contract.</summary>
    public async Task DeleteAsync(string containerName, string blobName,
        CancellationToken ct = default)
    {
        try
        {
            await client
                .DeleteObjectAsync(new DeleteObjectRequest { BucketName = containerName, Key = blobName }, ct)
                .ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            // Already gone - BlobDeleteWorkerService treats a missing target as a successful delete.
        }
    }

    /// <summary>Checks whether exists exists in the configured backend.</summary>
    public async Task<bool> ExistsAsync(string containerName, string blobName,
        CancellationToken ct = default)
    {
        try
        {
            await client
                .GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = containerName, Key = blobName }, ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    /// <summary>Loads requested data and maps missing records to the expected response.</summary>
    public async Task<Uri> GetBlobUriAsync(string containerName, string blobName,
        CancellationToken ct = default)
    {
        // Signed with the public client (Storage:S3:PublicServiceUrl): SigV4 signs the Host header into a
        // presigned URL, so a URL signed against the in-network client's host would fail signature
        // validation once a browser or caller resolved it against a different, public host.
        // GetPreSignedUrlRequest.Protocol defaults to HTTPS regardless of the client's own ServiceURL
        // scheme, so an http PublicServiceUrl (MinIO/local without TLS) must set it explicitly or the
        // signed URL would be unreachable.
        var protocol = settings.PublicServiceUrl is { } publicUrl
            && publicUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                ? Protocol.HTTP
                : Protocol.HTTPS;

        var url = await publicClient.GetPreSignedURLAsync(new GetPreSignedUrlRequest
        {
            BucketName = containerName,
            Key = blobName,
            Verb = HttpVerb.GET,
            Protocol = protocol,
            Expires = DateTime.UtcNow.Add(settings.DownloadUrlLifetime)
        }).ConfigureAwait(false);

        return new Uri(url);
    }
}
