using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Definition;
using EF.FlowEngine.Impl;
using EF.Messaging.RabbitMq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Bootstrapper;
using TaskFlow.Infrastructure.Messaging.RabbitMq;
using Test.Support;

namespace Test.Unit.Infrastructure;

/// <summary>
/// The engine's start-time clientRef check: workflow JSON seeding saves every definition through a registry that rejects
/// a clientRef the DI <see cref="IClientRegistry"/> does not provide, and that failure stops the host. Every clientRef of
/// every shipped workflow must therefore resolve against TaskFlow's own connector registrations, on both lanes
/// (NonAzure registers the RabbitMQ integration-events client, Azure the Service Bus one).
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class FlowEngineClientRegistrationTests
{
    private static readonly string[] ShippedWorkflowIds =
        ["ai-task-triage", "ai-task-decomposer", "compliance-check", "compliance-check-item"];

    [TestMethod]
    [DataRow("NonAzure")]
    [DataRow("Azure")]
    public async Task ShippedWorkflows_ResolveEveryClientRef_AgainstTheApplicationRegistrations(string lane)
    {
        await using var provider = BuildProvider(lane);
        var clients = provider.GetRequiredService<IClientRegistry>();
        var definitions = new JsonFileWorkflowRegistry("Workflows");

        foreach (var id in ShippedWorkflowIds)
        {
            var definition = await definitions.GetAsync(id, version: null, TestContext.CancellationToken);
            Assert.IsNotNull(definition, id);
            var errors = WorkflowDefinitionValidator.ValidateClients(definition, clients);
            Assert.IsEmpty(errors, $"{lane} {id}: {string.Join(" | ", errors)}");
        }
    }

    /// <summary>The check has teeth: a node naming a client the application does not register is reported.</summary>
    [TestMethod]
    public async Task UnregisteredClientRef_IsReported_AsClientNotRegistered()
    {
        await using var provider = BuildProvider("NonAzure");
        var clients = provider.GetRequiredService<IClientRegistry>();
        var json = System.Text.Json.Nodes.JsonNode.Parse(
            await File.ReadAllTextAsync(Path.Combine("Workflows", "ai-task-triage.json"), TestContext.CancellationToken))!;
        json["nodes"]!["n-apply-priority"]!["config"]!["clientRef"] = "taskflow-query";
        var definition = System.Text.Json.JsonSerializer.Deserialize<WorkflowDefinition>(
            json.ToJsonString(), WorkflowDefinitionJsonOptions.Default)!;

        var error = WorkflowDefinitionValidator.ValidateClients(definition, clients).Single();

        Assert.AreEqual(WorkflowDefinitionError.ClientNotRegistered, error.Code);
        Assert.AreEqual("n-apply-priority", error.NodeId);
    }

    private static ServiceProvider BuildProvider(string lane)
    {
        var settings = lane == "Azure"
            ? new Dictionary<string, string?>
            {
                ["Hosting:Lane"] = "Azure",
                ["ConnectionStrings:TaskFlowDbContextTrxn"] = SqlConnectionString,
                ["ConnectionStrings:TaskFlowDbContextQuery"] = SqlConnectionString,
                ["ConnectionStrings:BlobStorage1"] = "https://taskflowtest.blob.core.windows.net/",
                ["ConnectionStrings:TableStorage1"] = "https://taskflowtest.table.core.windows.net/",
                ["ConnectionStrings:CosmosDb1"] = "https://taskflowtest.documents.azure.com:443/",
                ["ServiceBus1:fullyQualifiedNamespace"] = "taskflowtest.servicebus.windows.net"
            }
            : new Dictionary<string, string?>
            {
                ["Hosting:Lane"] = "NonAzure",
                [$"{RabbitMqRegistration.OptionsSection}:ConnectionString"] = "amqp://guest:guest@localhost:5672/"
            };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings)
            .AddInMemoryCollection(TestColumnEncryption.Configuration)
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        if (lane == "Azure")
            services.RegisterInfrastructureServices(config);
        else
            services.AddTaskFlowRabbitMqMessaging(config);
        services.RegisterApplicationServices(config);
        return services.BuildServiceProvider();
    }

    private const string SqlConnectionString =
        "Server=localhost;Database=TaskFlowRegistration;User Id=sa;Password=NotARealPassword1!;TrustServerCertificate=true";

    public TestContext TestContext { get; set; } = null!;
}
