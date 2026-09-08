using Amazon.S3;
using Amazon.S3.Util;

namespace TaskFlow.Infrastructure.Storage.S3;

/// <summary>
/// Provisioning and connectivity surface for the S3 object-storage arm (D-037), kept free of any
/// <c>Amazon.*</c> type in its own signature so Host-layer callers (<c>EnsureExternalResources</c>,
/// <c>S3StorageHealthCheck</c>) can depend on it without referencing the AWS SDK directly - Test.Architecture
/// restricts <c>Amazon.*</c> references to this assembly alone.
/// </summary>
public interface IS3BucketProvisioner
{
    /// <summary>Creates the bucket if it does not already exist.</summary>
    Task EnsureBucketExistsAsync(string bucketName, CancellationToken ct = default);

    /// <summary>Confirms the configured client can reach the S3 endpoint; throws on failure.</summary>
    Task CheckConnectivityAsync(CancellationToken ct = default);
}

/// <inheritdoc cref="IS3BucketProvisioner"/>
internal sealed class S3BucketProvisioner(IAmazonS3 client) : IS3BucketProvisioner
{
    public async Task EnsureBucketExistsAsync(string bucketName, CancellationToken ct = default)
    {
        if (!await AmazonS3Util.DoesS3BucketExistV2Async(client, bucketName).ConfigureAwait(false))
            await client.PutBucketAsync(bucketName, ct).ConfigureAwait(false);
    }

    public Task CheckConnectivityAsync(CancellationToken ct = default) => client.ListBucketsAsync(ct);
}
