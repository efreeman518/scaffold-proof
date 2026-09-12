using Azure.Storage.Blobs;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Bootstrapper;
using TaskFlow.Hosting;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>Azure Blob Data Protection contract against Azurite.</summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class DataProtectionAzureBlobTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void TestSetup() =>
        IntegrationTestSetup.AssertAvailable("Azurite", AzuriteContainerFixture.StartupError);

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task ConnectionStringPersistence_CreatesContainer_AndSharesKeysAcrossProviders()
    {
        var originalLane = Environment.GetEnvironmentVariable(HostingLaneResolver.LaneEnvironmentVariable);
        Environment.SetEnvironmentVariable(HostingLaneResolver.LaneEnvironmentVariable, "Azure");
        var containerName = $"dp-{Guid.NewGuid():N}";

        try
        {
            await using var firstServices = BuildProvider("host-a", containerName);
            var first = firstServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("shared-purpose");
            var protectedPayload = first.Protect("shared payload");

            var container = new BlobServiceClient(AzuriteContainerFixture.ConnectionString)
                .GetBlobContainerClient(containerName);
            Assert.IsTrue((await container.ExistsAsync(TestContext.CancellationToken)).Value,
                "registration must create the configured container before Data Protection writes its key ring");

            await using var secondServices = BuildProvider("host-b", containerName);
            var second = secondServices.GetRequiredService<IDataProtectionProvider>().CreateProtector("shared-purpose");
            Assert.AreEqual("shared payload", second.Unprotect(protectedPayload));

            await container.DeleteIfExistsAsync(cancellationToken: TestContext.CancellationToken);
        }
        finally
        {
            Environment.SetEnvironmentVariable(HostingLaneResolver.LaneEnvironmentVariable, originalLane);
        }
    }

    private static ServiceProvider BuildProvider(string hostApplicationName, string containerName)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = hostApplicationName,
            EnvironmentName = Environments.Development,
            DisableDefaults = true
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [HostingLaneResolver.LaneConfigurationKey] = "Azure",
            [RegisterServices.DataProtectionPersistenceConfigKey] = "AzureBlob",
            ["ConnectionStrings:BlobStorage1"] = AzuriteContainerFixture.ConnectionString,
            ["DataProtection:AzureBlob:ContainerName"] = containerName,
            ["DataProtection:AzureBlob:BlobName"] = "shared-keys.xml",
            ["AppName"] = "TaskFlow.DataProtection.Integration"
        });
        builder.AddTaskFlowDataProtection(NullLogger.Instance);
        return builder.Services.BuildServiceProvider();
    }
}
