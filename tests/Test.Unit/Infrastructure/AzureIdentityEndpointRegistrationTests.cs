using Azure.Data.Tables;
using Azure.Messaging.ServiceBus;
using Azure.Storage.Blobs;
using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Clients.ServiceBus;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using TaskFlow.Bootstrapper;
using TaskFlow.Bootstrapper.HealthChecks;
using Test.Support;

namespace Test.Unit.Infrastructure;

/// <summary>Pins passwordless Azure data-plane registration to the endpoint keys emitted by Bicep.</summary>
[TestClass]
public sealed class AzureIdentityEndpointRegistrationTests
{
    private const string BlobEndpoint = "https://taskflowtest.blob.core.windows.net/";
    private const string TableEndpoint = "https://taskflowtest.table.core.windows.net/";
    private const string CosmosEndpoint = "https://taskflowtest.documents.azure.com:443/";
    private const string ServiceBusNamespace = "taskflowtest.servicebus.windows.net";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void AzureEndpoints_RegisterManagedIdentityClientsWithoutNetworkCalls()
    {
        var configuration = BuildAzureConfiguration();
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterInfrastructureServices(configuration);
        services.RegisterApplicationServices(configuration);

        using var provider = services.BuildServiceProvider();

        var blob = provider.GetRequiredService<IAzureClientFactory<BlobServiceClient>>()
            .CreateClient("TaskFlowBlobClient");
        var table = provider.GetRequiredService<IAzureClientFactory<TableServiceClient>>()
            .CreateClient("TaskFlowTableClient");
        var serviceBus = provider.GetRequiredService<IAzureClientFactory<ServiceBusClient>>()
            .CreateClient("TaskFlowSBClient");
        var cosmos = provider.GetRequiredService<CosmosClient>();

        Assert.AreEqual(new Uri(BlobEndpoint), blob.Uri);
        Assert.AreEqual(new Uri(TableEndpoint), table.Uri);
        Assert.AreEqual(ServiceBusNamespace, serviceBus.FullyQualifiedNamespace);
        Assert.AreEqual(new Uri(CosmosEndpoint), cosmos.Endpoint);

        var flowEngineClient = provider.GetServices<IFlowClient>()
            .Single(client => client.ClientRef == "integration-events");
        Assert.IsInstanceOfType<ServiceBusMessageClient>(flowEngineClient);

        var healthChecks = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value;
        Assert.IsTrue(healthChecks.Registrations.Any(registration => registration.Name == "service-bus"));
    }

    [TestMethod]
    public void AzureConnectionStrings_StillRegisterEmulatorClients()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hosting:Lane"] = "Azure",
                ["ConnectionStrings:TaskFlowDbContextTrxn"] = SqlConnectionString,
                ["ConnectionStrings:TaskFlowDbContextQuery"] = SqlConnectionString,
                ["ConnectionStrings:BlobStorage1"] = "UseDevelopmentStorage=true",
                ["ConnectionStrings:TableStorage1"] = "UseDevelopmentStorage=true",
                ["ConnectionStrings:CosmosDb1"] = CosmosEmulatorConnectionString,
                ["ConnectionStrings:ServiceBus1"] = ServiceBusEmulatorConnectionString
            })
            .AddInMemoryCollection(TestColumnEncryption.Configuration)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.RegisterInfrastructureServices(configuration);

        using var provider = services.BuildServiceProvider();

        var blob = provider.GetRequiredService<IAzureClientFactory<BlobServiceClient>>()
            .CreateClient("TaskFlowBlobClient");
        var table = provider.GetRequiredService<IAzureClientFactory<TableServiceClient>>()
            .CreateClient("TaskFlowTableClient");
        var serviceBus = provider.GetRequiredService<IAzureClientFactory<ServiceBusClient>>()
            .CreateClient("TaskFlowSBClient");
        var cosmos = provider.GetRequiredService<CosmosClient>();

        Assert.AreEqual("127.0.0.1", blob.Uri.Host);
        Assert.AreEqual("127.0.0.1", table.Uri.Host);
        Assert.AreEqual("localhost", serviceBus.FullyQualifiedNamespace);
        Assert.AreEqual("localhost", cosmos.Endpoint.Host);
    }

    [TestMethod]
    public void DataProtectionEndpointRegistration_DoesNotContactBlobStorage()
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Test.Unit",
            EnvironmentName = Environments.Production,
            DisableDefaults = true
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hosting:Lane"] = "Azure",
            ["ConnectionStrings:BlobStorage1"] = "https://127.0.0.1:1/"
        });

        RegisterServices.AddTaskFlowDataProtection(builder, NullLogger.Instance);

        Assert.IsTrue(builder.Services.Any(
            descriptor => descriptor.ServiceType.FullName?.Contains("DataProtection", StringComparison.Ordinal) == true));
    }

    [TestMethod]
    public void FlowEngineServiceBusTopic_DefaultsToDomainEvents_AndAllowsOverride()
    {
        Assert.AreEqual("DomainEvents", RegisterServices.ResolveFlowEngineServiceBusTopic(
            new ConfigurationBuilder().Build()));

        var configured = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DomainEventsTopic"] = "trigger-topic",
                ["FlowEngine:ServiceBusTopic"] = "workflow-topic"
            })
            .Build();
        Assert.AreEqual("workflow-topic", RegisterServices.ResolveFlowEngineServiceBusTopic(configured));
    }

    [TestMethod]
    public async Task ServiceBusHealthCheck_CreatesAnEmptyBatchWithoutPublishing()
    {
        var batch = ServiceBusModelFactory.ServiceBusMessageBatch(
            1024,
            [],
            new CreateMessageBatchOptions(),
            _ => true);
        var sender = new Mock<ServiceBusSender>();
        sender.Setup(value => value.CreateMessageBatchAsync(It.IsAny<CancellationToken>()))
            .Returns(new ValueTask<ServiceBusMessageBatch>(batch));
        sender.Setup(value => value.DisposeAsync()).Returns(ValueTask.CompletedTask);

        var client = new Mock<ServiceBusClient>();
        client.Setup(value => value.CreateSender("DomainEvents")).Returns(sender.Object);
        var factory = new Mock<IAzureClientFactory<ServiceBusClient>>();
        factory.Setup(value => value.CreateClient("TaskFlowSBClient")).Returns(client.Object);
        var healthCheck = new ServiceBusHealthCheck(factory.Object, new ConfigurationBuilder().Build());

        var result = await healthCheck.CheckHealthAsync(
            new HealthCheckContext(),
            TestContext.CancellationToken);

        Assert.AreEqual(HealthStatus.Healthy, result.Status);
        sender.Verify(value => value.CreateMessageBatchAsync(It.IsAny<CancellationToken>()), Times.Once);
        sender.Verify(
            value => value.SendMessagesAsync(It.IsAny<ServiceBusMessageBatch>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [TestMethod]
    public void AzureBicep_EmitsBootstrapperEndpointKeyShapes()
    {
        var main = File.ReadAllText(RepoRoot.Combine("infra", "main.bicep"));
        var functions = File.ReadAllText(RepoRoot.Combine("infra", "modules", "functions.bicep"));

        StringAssert.Contains(main, "{ name: 'ConnectionStrings__BlobStorage1', value: storage.outputs.appStorageBlobEndpoint }");
        StringAssert.Contains(main, "{ name: 'ConnectionStrings__TableStorage1', value: storage.outputs.appStorageTableEndpoint }");
        StringAssert.Contains(main, "{ name: 'ConnectionStrings__CosmosDb1', value: cosmosDb.outputs.accountEndpoint }");
        StringAssert.Contains(main, "{ name: 'ServiceBus1__fullyQualifiedNamespace', value: serviceBus.outputs.namespaceEndpoint }");
        StringAssert.Contains(functions, "{ name: 'ServiceBus1__fullyQualifiedNamespace', value: serviceBusNamespace }");
    }

    private static IConfiguration BuildAzureConfiguration() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hosting:Lane"] = "Azure",
            ["ConnectionStrings:TaskFlowDbContextTrxn"] = SqlConnectionString,
            ["ConnectionStrings:TaskFlowDbContextQuery"] = SqlConnectionString,
            ["ConnectionStrings:BlobStorage1"] = BlobEndpoint,
            ["ConnectionStrings:TableStorage1"] = TableEndpoint,
            ["ConnectionStrings:CosmosDb1"] = CosmosEndpoint,
            ["ServiceBus1:fullyQualifiedNamespace"] = ServiceBusNamespace,
            ["HealthChecks:EnableExternalServices"] = "true"
        })
        .AddInMemoryCollection(TestColumnEncryption.Configuration)
        .Build();

    private const string SqlConnectionString =
        "Server=localhost;Database=TaskFlowRegistration;User Id=sa;Password=NotARealPassword1!;TrustServerCertificate=true";

    private const string CosmosEmulatorConnectionString =
        "AccountEndpoint=https://localhost:8081/;" +
        "AccountKey=QUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUE=;";

    private const string ServiceBusEmulatorConnectionString =
        "Endpoint=sb://localhost;SharedAccessKeyName=RootManageSharedAccessKey;" +
        "SharedAccessKey=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=;UseDevelopmentEmulator=true;";
}
