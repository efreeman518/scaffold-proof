using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Bootstrapper;
using TaskFlow.Infrastructure.AI;
using TaskFlow.Infrastructure.AI.Agents;
using TaskFlow.Infrastructure.AI.Search;

namespace Test.Unit.AI;

/// <summary>
/// Validates <c>AddAiServices</c> DI wiring against an in-memory <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>:
/// without a registered <see cref="IChatClient"/>, the No-Op search/agent and a no-op IChatClient are
/// registered; with a real IChatClient, the live agent is wired; settings bind to
/// <c>TaskFlowAiSettings</c>.
/// Pure-unit tier (in-memory ServiceCollection): no Azure client, no real endpoint.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class AiServiceRegistrationTests
{
    [TestMethod]
    public async Task RegisterAiChatClientAsync_WithAzureConnection_RecordsAzureProvider()
    {
        var builder = CreateHostBuilder(new Dictionary<string, string?>
        {
            ["ConnectionStrings:chat"] = "Endpoint=https://example.services.ai.azure.com/;Key=fake",
            ["AiServices:DisableFoundryLocal"] = "false"
        });

        await builder.RegisterAiChatClientAsync(NullLogger.Instance, TestContext.CancellationToken);

        var provider = builder.Services.BuildServiceProvider();
        Assert.AreEqual("azure", provider.GetRequiredService<AiProviderInfo>().Name);
    }

    [TestMethod]
    public async Task RegisterAiChatClientAsync_WithFoundryLocalDisabled_AllowsNoOpFallback()
    {
        var builder = CreateHostBuilder(new Dictionary<string, string?>
        {
            ["AiServices:DisableFoundryLocal"] = "true"
        });

        await builder.RegisterAiChatClientAsync(NullLogger.Instance, TestContext.CancellationToken);
        builder.Services.AddAiServices(builder.Configuration);

        var provider = builder.Services.BuildServiceProvider();
        Assert.IsInstanceOfType<NoOpChatClient>(provider.GetRequiredService<IChatClient>());
        Assert.AreEqual("none", provider.GetRequiredService<AiProviderInfo>().Name);
    }

    [TestMethod]
    public async Task RegisterAiChatClientAsync_WithFoundryLocalBootstrapFailure_AllowsNoOpFallback()
    {
        var builder = CreateHostBuilder(new Dictionary<string, string?>
        {
            ["ConnectionStrings:chat"] = "",
            ["AiServices:DisableFoundryLocal"] = "false",
            ["AiServices:LocalWebUrl"] = "not-a-url"
        });

        await builder.RegisterAiChatClientAsync(NullLogger.Instance, TestContext.CancellationToken);
        builder.Services.AddAiServices(builder.Configuration);

        var provider = builder.Services.BuildServiceProvider();
        Assert.IsInstanceOfType<NoOpChatClient>(provider.GetRequiredService<IChatClient>());
        Assert.AreEqual("none", provider.GetRequiredService<AiProviderInfo>().Name);
    }

    [TestMethod]
    public async Task RegisterAiChatClientAsync_WithRequiredFoundryLocalBootstrapFailure_Throws()
    {
        var builder = CreateHostBuilder(new Dictionary<string, string?>
        {
            ["ConnectionStrings:chat"] = "",
            ["AiServices:DisableFoundryLocal"] = "false",
            ["AiServices:RequireFoundryLocal"] = "true",
            ["AiServices:LocalModel"] = "",
            ["AiServices:LocalWebUrl"] = "not-a-url"
        });

        await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            builder.RegisterAiChatClientAsync(NullLogger.Instance, TestContext.CancellationToken));
    }

    /// <summary>Verifies add AI services with no config registers no op services behavior and protects the expected test contract.</summary>
    [TestMethod]
    public void AddAiServices_WithNoConfig_RegistersNoOpServices()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiServices:UseSearch"] = "false",
                ["AiServices:UseAgents"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // NoOpSearchService answers from the SQL prefix search, so the repository must resolve.
        services.AddSingleton(Mock.Of<ITaskItemRepositoryQuery>());
        services.AddAiServices(config);

        var provider = services.BuildServiceProvider();

        var searchService = provider.GetRequiredService<ITaskFlowSearchService>();
        var agentService = provider.GetRequiredService<ITaskAssistantAgent>();
        var chatClient = provider.GetRequiredService<IChatClient>();
        var providerInfo = provider.GetRequiredService<AiProviderInfo>();

        Assert.IsInstanceOfType<NoOpSearchService>(searchService);
        Assert.IsInstanceOfType<NoOpTaskAssistantAgent>(agentService);
        Assert.IsInstanceOfType<NoOpChatClient>(chatClient);
        Assert.AreEqual("none", providerInfo.Name);
    }

    /// <summary>With a real IChatClient registered, the live agent is wired.</summary>
    [TestMethod]
    public void AddAiServices_WithChatClient_RegistersLiveAgent()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiServices:UseAgents"] = "false"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // NoOpSearchService answers from the SQL prefix search, so the repository must resolve.
        services.AddSingleton(Mock.Of<ITaskItemRepositoryQuery>());
        // Simulate the host having wired a Foundry IChatClient before AddAiServices runs.
        services.AddSingleton(new Mock<IChatClient>().Object);
        services.AddSingleton(new AiProviderInfo("local"));

        services.AddAiServices(config);

        // Assert the descriptor (do not build/resolve - constructing the agent would require the full
        // application service graph that TaskItemTools depends on).
        var descriptor = services.Single(d => d.ServiceType == typeof(ITaskAssistantAgent));
        Assert.AreEqual(typeof(TaskAssistantAgentService), descriptor.ImplementationType);

        // The real IChatClient must be left in place (no NoOpChatClient added on top).
        Assert.DoesNotContain(d => d.ImplementationType == typeof(NoOpChatClient), services);

        var provider = services.BuildServiceProvider();
        Assert.AreEqual("local", provider.GetRequiredService<AiProviderInfo>().Name);
    }

    /// <summary>Verifies add AI services with search enabled no endpoint registers no op search behavior and protects the expected test contract.</summary>
    [TestMethod]
    public void AddAiServices_WithSearchEnabled_NoEndpoint_RegistersNoOpSearch()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiServices:UseSearch"] = "true",
                ["AiServices:SearchEndpoint"] = ""
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // NoOpSearchService answers from the SQL prefix search, so the repository must resolve.
        services.AddSingleton(Mock.Of<ITaskItemRepositoryQuery>());
        services.AddAiServices(config);

        var provider = services.BuildServiceProvider();
        var searchService = provider.GetRequiredService<ITaskFlowSearchService>();

        Assert.IsInstanceOfType<NoOpSearchService>(searchService);
    }

    /// <summary>Verifies add AI services with agents enabled no endpoint registers no op agent behavior and protects the expected test contract.</summary>
    [TestMethod]
    public void AddAiServices_WithAgentsEnabled_NoEndpoint_RegistersNoOpAgent()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiServices:UseAgents"] = "true",
                ["AiServices:FoundryEndpoint"] = ""
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // NoOpSearchService answers from the SQL prefix search, so the repository must resolve.
        services.AddSingleton(Mock.Of<ITaskItemRepositoryQuery>());
        services.AddAiServices(config);

        var provider = services.BuildServiceProvider();
        var agentService = provider.GetRequiredService<ITaskAssistantAgent>();

        Assert.IsInstanceOfType<NoOpTaskAssistantAgent>(agentService);
    }

    /// <summary>Verifies add AI services settings bind correctly behavior and protects the expected test contract.</summary>
    [TestMethod]
    public void AddAiServices_SettingsBindCorrectly()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["AiServices:UseSearch"] = "true",
                ["AiServices:UseAgents"] = "false",
                ["AiServices:SearchIndexName"] = "custom-index",
                ["AiServices:AgentModelDeployment"] = "gpt-4o-custom"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        // NoOpSearchService answers from the SQL prefix search, so the repository must resolve.
        services.AddSingleton(Mock.Of<ITaskItemRepositoryQuery>());
        services.AddAiServices(config);

        var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<Microsoft.Extensions.Options.IOptions<TaskFlowAiSettings>>();

        Assert.IsTrue(options.Value.UseSearch);
        Assert.IsFalse(options.Value.UseAgents);
        Assert.AreEqual("custom-index", options.Value.SearchIndexName);
        Assert.AreEqual("gpt-4o-custom", options.Value.AgentModelDeployment);
    }

    private static HostApplicationBuilder CreateHostBuilder(Dictionary<string, string?> settings)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ApplicationName = "Test.Unit",
            EnvironmentName = "Testing",
            DisableDefaults = true
        });
        builder.Configuration.AddInMemoryCollection(settings);
        builder.Services.AddLogging();
        return builder;
    }

    public TestContext TestContext { get; set; } = null!;
}
