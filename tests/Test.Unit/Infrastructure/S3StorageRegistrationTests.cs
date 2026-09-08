using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Bootstrapper;
using TaskFlow.Infrastructure.Storage.S3;

namespace Test.Unit.Infrastructure;

/// <summary>
/// Coverage for the D-037 S3 object-storage arm: the eager <c>Storage:S3:PublicServiceUrl</c> fail-fast
/// (presigned download URLs are host-bound, so a missing public endpoint is a configuration error the
/// moment the arm is selected) and that the arm resolves <see cref="IBlobStorageRepository"/> to
/// <see cref="S3ObjectStorageRepository"/> in a plain DI container. <c>RegisterServices.AddS3StorageServices</c>
/// is internal, exercised directly here via the <c>InternalsVisibleTo</c> grant on <c>TaskFlow.Bootstrapper</c>
/// (the same grant <c>ProviderSwitchArchitectureTests</c> relies on via reflection for every switch's default arm).
/// Test.Endpoints exercises attachments against an in-memory fake <c>IBlobStorageRepository</c>, not this
/// arm, so DI-only coverage for it belongs here.
/// Pure-unit tier (in-memory IConfiguration / ServiceCollection): no live S3/MinIO endpoint.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class S3StorageRegistrationTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => e.Value))
            .Build();

    [TestMethod]
    public void AddS3StorageServices_MissingPublicServiceUrl_Throws()
    {
        var services = new ServiceCollection();
        var config = Config(("Storage:S3:ServiceUrl", "http://minio:9000"));

        var ex = Assert.ThrowsExactly<InvalidOperationException>(
            () => RegisterServices.AddS3StorageServices(services, config));
        StringAssert.Contains(ex.Message, "PublicServiceUrl");
    }

    [TestMethod]
    public void AddS3StorageServices_Configured_ResolvesS3ObjectStorageRepository()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var config = Config(
            ("Storage:S3:ServiceUrl", "http://minio:9000"),
            ("Storage:S3:PublicServiceUrl", "http://localhost:9000"),
            ("Storage:S3:AccessKeyId", "test-key"),
            ("Storage:S3:SecretAccessKey", "test-secret"));

        RegisterServices.AddS3StorageServices(services, config);
        using var provider = services.BuildServiceProvider();

        Assert.IsInstanceOfType<S3ObjectStorageRepository>(provider.GetRequiredService<IBlobStorageRepository>());
    }

    [TestMethod]
    public void AttachmentBlobs_BlobName_UsedDirectlyAsTheS3ObjectKey()
    {
        // S3ObjectStorageRepository applies no transformation of its own: the blobName argument it
        // receives becomes the S3 object Key verbatim, so the shared naming convention IS the S3 key
        // convention. This pins that convention so a change here is a deliberate, visible decision.
        var tenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var ownerId = Guid.Parse("22222222-2222-2222-2222-222222222222");

        Assert.AreEqual(
            $"{tenantId}/{ownerId}/report.pdf",
            AttachmentBlobs.BlobName(tenantId, ownerId, "report.pdf"));
    }
}
