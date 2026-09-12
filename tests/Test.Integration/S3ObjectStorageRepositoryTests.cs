using Amazon.S3;
using Amazon.S3.Model;
using EF.Storage.S3;
using Microsoft.Extensions.DependencyInjection;
using System.Text;
using TaskFlow.Hosting;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// Validates <c>S3ObjectStorageRepository</c> against a real SeaweedFS container (D-037): the upload/exists/
/// download/delete round trip, delete-of-a-missing-key success (matches <c>BlobDeleteWorkerService</c>'s
/// already-gone semantics), x-amz-meta- metadata round trip, and that a presigned URL is signed against
/// <c>Storage:S3:PublicServiceUrl</c> and actually resolves.
/// Component tier: exercises only SeaweedFS via a standalone <c>SeaweedFsContainerFixture</c> (started by
/// <c>IntegrationTestSetup</c>) - no API, no Function, no Aspire graph.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public class S3ObjectStorageRepositoryTests
{
    /// <summary>Classifies Docker unavailability separately from a SeaweedFS startup failure.</summary>
    [TestInitialize]
    public void TestSetup()
    {
        IntegrationTestSetup.RequireLane(HostingLane.NonAzure);
        IntegrationTestSetup.AssertAvailable("SeaweedFS", SeaweedFsContainerFixture.StartupError);
    }

    /// <summary>Verifies upload, existence, download, list, delete, and metadata against SeaweedFS.</summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Given_UploadedBlob_When_ExistsDownloadListDeleteAgainstSeaweedFs_Then_RoundTripAndMetadataMatch()
    {
        var ct = TestContext.CancellationToken;
        await using var scope = await CreateRepositoryAsync(ct);
        var (repository, client, bucket) = scope;

        try
        {
            const string blobName = "11111111-1111-1111-1111-111111111111/22222222-2222-2222-2222-222222222222/report.pdf";
            var content = Encoding.UTF8.GetBytes("s3 integration payload");

            Assert.IsFalse(await repository.ExistsAsync(bucket, blobName, ct), "must not exist before upload");

            using (var upload = new MemoryStream(content))
            {
                await repository.UploadAsync(bucket, blobName, upload, "application/pdf",
                    new Dictionary<string, string> { ["owner"] = "integration-test" }, ct);
            }

            Assert.IsTrue(await repository.ExistsAsync(bucket, blobName, ct));

            var listed = await client.ListObjectsV2Async(new ListObjectsV2Request { BucketName = bucket }, ct);
            CollectionAssert.Contains((listed.S3Objects ?? []).Select(e => e.Key).ToList(), blobName);

            using var getObjectResponse = await client.GetObjectAsync(bucket, blobName, ct);
            Assert.AreEqual("integration-test", getObjectResponse.Metadata["owner"]);

            await using (var downloaded = await repository.DownloadAsync(bucket, blobName, ct))
            using (var reader = new StreamReader(downloaded))
            {
                Assert.AreEqual("s3 integration payload", await reader.ReadToEndAsync(ct));
            }

            await repository.DeleteAsync(bucket, blobName, ct);
            Assert.IsFalse(await repository.ExistsAsync(bucket, blobName, ct));

            // Deleting an already-gone key succeeds - BlobDeleteWorkerService relies on this to complete leases.
            await repository.DeleteAsync(bucket, blobName, ct);
        }
        finally { await client.DeleteBucketAsync(bucket, ct); }
    }

    /// <summary>Verifies given a blob, when a presigned URL is requested, then it is signed for PublicServiceUrl and resolves the content.</summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Given_Blob_When_PresignedUrlRequested_Then_SignedForPublicServiceUrlAndResolvesContent()
    {
        var ct = TestContext.CancellationToken;
        await using var scope = await CreateRepositoryAsync(ct);
        var (repository, client, bucket) = scope;

        try
        {
            const string blobName = "tenant/owner/presigned.txt";
            using (var upload = new MemoryStream(Encoding.UTF8.GetBytes("presigned content")))
            {
                await repository.UploadAsync(bucket, blobName, upload, "text/plain", cancellationToken: ct);
            }

            var uri = await repository.GetPresignedUrlAsync(bucket, blobName, TimeSpan.FromHours(1), cancellationToken: ct);
            StringAssert.StartsWith(uri.ToString(), SeaweedFsContainerFixture.ServiceUrl,
                "the presigned URL must be signed for and reachable at Storage:S3:PublicServiceUrl");

            using var http = new HttpClient();
            var response = await http.GetAsync(uri, ct);
            response.EnsureSuccessStatusCode();
            Assert.AreEqual("presigned content", await response.Content.ReadAsStringAsync(ct));
        }
        finally
        {
            await client.DeleteObjectAsync(bucket, "tenant/owner/presigned.txt", ct);
            await client.DeleteBucketAsync(bucket, ct);
        }
    }

    /// <summary>Builds the published repository against SeaweedFS and proves its provisioner creates the bucket.</summary>
    private static async Task<S3RepositoryScope> CreateRepositoryAsync(CancellationToken ct)
    {
        var settings = new S3StorageSettings
        {
            ServiceUrl = SeaweedFsContainerFixture.ServiceUrl,
            // No separate in-network/public split under Testcontainers: the fixture's mapped host/port
            // already reflects TESTCONTAINERS_HOST_OVERRIDE, so it is reachable the same way for both
            // the repository's client and this test's own HttpClient (see SeaweedFsContainerFixture remarks).
            PublicServiceUrl = SeaweedFsContainerFixture.ServiceUrl,
            AccessKeyId = SeaweedFsContainerFixture.AccessKey,
            SecretAccessKey = SeaweedFsContainerFixture.SecretKey,
            ForcePathStyle = true
        };

        var bucket = $"attachments-{Guid.NewGuid():N}";
        var services = new ServiceCollection();
        services.AddS3ObjectStorage(settings);
        var provider = services.BuildServiceProvider();
        var client = provider.GetRequiredService<IAmazonS3>();

        var before = await client.ListBucketsAsync(ct);
        Assert.IsFalse(before.Buckets?.Any(e => e.BucketName == bucket) ?? false,
            "the fixture must not pre-create the bucket");
        await provider.GetRequiredService<IS3BucketProvisioner>().EnsureBucketExistsAsync(bucket, ct);
        var after = await client.ListBucketsAsync(ct);
        Assert.IsTrue(after.Buckets?.Any(e => e.BucketName == bucket) ?? false,
            "EF.Storage.S3 provisioner must create the bucket");

        return new S3RepositoryScope(
            provider,
            (S3ObjectStorageRepository)provider.GetRequiredService<EF.Storage.Contracts.IObjectStorageRepository>(),
            client,
            bucket);
    }

    private sealed record S3RepositoryScope(
        ServiceProvider Services,
        S3ObjectStorageRepository Repository,
        IAmazonS3 Client,
        string Bucket) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync() => await Services.DisposeAsync();

        public void Deconstruct(out S3ObjectStorageRepository repository, out IAmazonS3 client, out string bucket) =>
            (repository, client, bucket) = (Repository, Client, Bucket);
    }

    public TestContext TestContext { get; set; } = null!;
}
