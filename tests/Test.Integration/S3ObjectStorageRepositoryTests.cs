using Amazon.Runtime;
using Amazon.S3;
using EF.Storage.S3;
using System.Text;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// Validates <c>S3ObjectStorageRepository</c> against a real MinIO container (D-037): the upload/exists/
/// download/delete round trip, delete-of-a-missing-key success (matches <c>BlobDeleteWorkerService</c>'s
/// already-gone semantics), x-amz-meta- metadata round trip, and that a presigned URL is signed against
/// <c>Storage:S3:PublicServiceUrl</c> and actually resolves.
/// Component tier: exercises only MinIO via a standalone <c>MinioContainerFixture</c> (started by
/// <c>IntegrationTestSetup</c>) - no API, no Function, no Aspire graph.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public class S3ObjectStorageRepositoryTests
{
    /// <summary>Classifies Docker unavailability separately from a MinIO startup failure.</summary>
    [TestInitialize]
    public void TestSetup()
    {
        IntegrationTestSetup.AssertAvailable("MinIO", MinioContainerFixture.StartupError);
    }

    /// <summary>Verifies given uploaded blob, when exists-download-delete run against MinIO, then round trip and metadata match.</summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Given_UploadedBlob_When_ExistsDownloadDeleteAgainstMinio_Then_RoundTripAndMetadataMatch()
    {
        var ct = TestContext.CancellationToken;
        var (repository, client, bucket) = await CreateRepositoryAsync(ct);

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
        finally
        {
            await client.DeleteBucketAsync(bucket, ct);
        }
    }

    /// <summary>Verifies given a blob, when a presigned URL is requested, then it is signed for PublicServiceUrl and resolves the content.</summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Given_Blob_When_PresignedUrlRequested_Then_SignedForPublicServiceUrlAndResolvesContent()
    {
        var ct = TestContext.CancellationToken;
        var (repository, client, bucket) = await CreateRepositoryAsync(ct);

        try
        {
            const string blobName = "tenant/owner/presigned.txt";
            using (var upload = new MemoryStream(Encoding.UTF8.GetBytes("presigned content")))
            {
                await repository.UploadAsync(bucket, blobName, upload, "text/plain", cancellationToken: ct);
            }

            var uri = await repository.GetPresignedUrlAsync(bucket, blobName, TimeSpan.FromHours(1), cancellationToken: ct);
            StringAssert.StartsWith(uri.ToString(), MinioContainerFixture.ServiceUrl,
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

    /// <summary>Builds a repository and its backing client against the MinIO fixture, with a fresh bucket created for the test.</summary>
    private static async Task<(S3ObjectStorageRepository Repository, IAmazonS3 Client, string Bucket)> CreateRepositoryAsync(CancellationToken ct)
    {
        var settings = new S3StorageSettings
        {
            ServiceUrl = MinioContainerFixture.ServiceUrl,
            // No separate in-network/public split under Testcontainers: the fixture's mapped host/port
            // already reflects TESTCONTAINERS_HOST_OVERRIDE, so it is reachable the same way for both
            // the repository's client and this test's own HttpClient (see MinioContainerFixture remarks).
            PublicServiceUrl = MinioContainerFixture.ServiceUrl,
            AccessKeyId = MinioContainerFixture.AccessKey,
            SecretAccessKey = MinioContainerFixture.SecretKey,
            ForcePathStyle = true
        };

        var config = new AmazonS3Config { ServiceURL = settings.ServiceUrl, ForcePathStyle = true, AuthenticationRegion = settings.Region };
        var credentials = new BasicAWSCredentials(settings.AccessKeyId, settings.SecretAccessKey);
        var client = new AmazonS3Client(credentials, config);

        var bucket = $"attachments-{Guid.NewGuid():N}";
        await client.PutBucketAsync(bucket, ct);

        return (new S3ObjectStorageRepository(client, client, settings), client, bucket);
    }

    public TestContext TestContext { get; set; } = null!;
}
