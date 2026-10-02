using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Definition;
using EF.FlowEngine.Impl;
using EF.Messaging.RabbitMq;
using TaskFlow.Infrastructure.Messaging.RabbitMq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Bootstrapper;

namespace Test.Unit.Infrastructure;

/// <summary>
/// The engine's start-time clientRef check (EF.FlowEngine 1.0.202): workflow JSON seeding saves every definition through a
/// registry that rejects a clientRef the DI <see cref="IClientRegistry"/> does not provide, and that failure stops the
/// host. Every clientRef of every shipped workflow must therefore resolve against TaskFlow's own connector registrations.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class FlowEngineClientRegistrationTests
{
    [TestMethod]
    public async Task ShippedWorkflows_ResolveEveryClientRef_AgainstTheApplicationRegistrations()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hosting:Lane"] = "NonAzure",
                [$"{RabbitMqRegistration.OptionsSection}:ConnectionString"] = "amqp://guest:guest@localhost:5672/"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTaskFlowRabbitMqMessaging(config);
        services.RegisterApplicationServices(config);

        await using var provider = services.BuildServiceProvider();
        var clients = provider.GetRequiredService<IClientRegistry>();
        var definitions = new JsonFileWorkflowRegistry("Workflows");

        foreach (var id in new[] { "ai-task-triage", "ai-task-decomposer", "compliance-check" })
        {
            var definition = await definitions.GetAsync(id, version: null, TestContext.CancellationToken);
            Assert.IsNotNull(definition, id);
            var errors = WorkflowDefinitionValidator.ValidateClients(definition, clients);
            Assert.IsEmpty(errors, $"{id}: {string.Join(" | ", errors)}");
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
