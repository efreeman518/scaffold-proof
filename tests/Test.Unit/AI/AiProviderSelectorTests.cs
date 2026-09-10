using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Application.Contracts.Configuration;
using TaskFlow.Bootstrapper;
using TaskFlow.Infrastructure.AI;

namespace Test.Unit.AI;

/// <summary>
/// Selector-table coverage for the D-041 LLM provider switch's explicit values and D-035 lane default,
/// layered on top of <see cref="RegisterServices.RegisterAiChatClientAsync"/>'s existing legacy-derivation
/// tests in <c>AiServiceRegistrationTests</c> (which stay green: they never set <c>AiServices:Provider</c>,
/// so <see cref="RegisterServices.ResolveAiProvider"/> resolves null there and the derivation is untouched).
/// Pure-unit tier (in-memory IConfiguration / HostApplicationBuilder): no live Azure or Foundry endpoint.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class AiProviderSelectorTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => e.Value))
            .Build();

    [TestMethod]
    public void ResolveAiProvider_Unset_AzureLane_ReturnsNull_ForCallerDerivedDefault() =>
        Assert.IsNull(RegisterServices.ResolveAiProvider(Config()));

    [TestMethod]
    public void ResolveAiProvider_PortableLane_DefaultsToOpenAICompatible() =>
        Assert.AreEqual(
            AiProvider.OpenAICompatible,
            RegisterServices.ResolveAiProvider(Config((HostingLaneSelector.ConfigurationKey, "Portable"))));

    [TestMethod]
    public void ResolveAiProvider_ConfigBeatsLaneDefault() =>
        Assert.AreEqual(
            AiProvider.None,
            RegisterServices.ResolveAiProvider(Config(
                (HostingLaneSelector.ConfigurationKey, "Portable"),
                (RegisterServices.AiProviderConfigKey, "None"))));

    [TestMethod]
    public void ResolveAiProvider_UnknownValue_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            RegisterServices.ResolveAiProvider(Config((RegisterServices.AiProviderConfigKey, "Bedrock"))));

    [TestMethod]
    [DoNotParallelize]
    public void ResolveAiProvider_EnvWinsOverConfig()
    {
        var original = Environment.GetEnvironmentVariable(RegisterServices.AiProviderEnvVar);
        Environment.SetEnvironmentVariable(RegisterServices.AiProviderEnvVar, "None");
        try
        {
            Assert.AreEqual(
                AiProvider.None,
                RegisterServices.ResolveAiProvider(Config((RegisterServices.AiProviderConfigKey, "AzureInference"))));
        }
        finally
        {
            Environment.SetEnvironmentVariable(RegisterServices.AiProviderEnvVar, original);
        }
    }

    [TestMethod]
    public async Task RegisterAiChatClientAsync_OpenAICompatible_MissingEndpoint_ThrowsFailFast()
    {
        var builder = CreateHostBuilder(new Dictionary<string, string?>
        {
            [RegisterServices.AiProviderConfigKey] = "OpenAICompatible",
            ["AiServices:ApiKey"] = "fake-key"
        });

        var ex = await AssertStartupValidationFailsAsync(builder);
        StringAssert.Contains(ex.Message, "Endpoint");
    }

    [TestMethod]
    public async Task RegisterAiChatClientAsync_OpenAICompatible_MissingApiKey_ThrowsFailFast()
    {
        var builder = CreateHostBuilder(new Dictionary<string, string?>
        {
            [RegisterServices.AiProviderConfigKey] = "OpenAICompatible",
            ["AiServices:Endpoint"] = "https://api.example.com/v1"
        });

        var ex = await AssertStartupValidationFailsAsync(builder);
        StringAssert.Contains(ex.Message, "ApiKey");
    }

    /// <summary>
    /// EF.AI validates the bound settings through <c>ValidateOnStart</c>, so an explicitly selected
    /// OpenAICompatible provider with a missing endpoint, key, or model fails at host start rather than at
    /// registration. That is still before the host serves a request, and it is what keeps EF.AI from
    /// silently handing every caller a disabled client.
    /// </summary>
    private async Task<AggregateException> AssertStartupValidationFailsAsync(
        HostApplicationBuilder builder)
    {
        await builder.RegisterAiChatClientAsync(NullLogger.Instance, TestContext.CancellationToken);

        using var host = builder.Build();
        // Both the chat client and the embedding generator validate, so StartupValidator aggregates the
        // two OptionsValidationExceptions rather than surfacing one.
        return await Assert.ThrowsExactlyAsync<AggregateException>(
            () => host.StartAsync(TestContext.CancellationToken));
    }

    [TestMethod]
    public async Task RegisterAiChatClientAsync_OpenAICompatible_EndpointAndKeyPresent_RegistersBothClients()
    {
        var builder = CreateHostBuilder(new Dictionary<string, string?>
        {
            [RegisterServices.AiProviderConfigKey] = "OpenAICompatible",
            ["AiServices:Endpoint"] = "https://api.example.com/v1",
            ["AiServices:ApiKey"] = "fake-key"
        });

        await builder.RegisterAiChatClientAsync(NullLogger.Instance, TestContext.CancellationToken);

        var provider = builder.Services.BuildServiceProvider();
        Assert.AreEqual("openai-compatible", provider.GetRequiredService<AiProviderInfo>().Name);
        Assert.IsNotNull(provider.GetRequiredService<Microsoft.Extensions.AI.IChatClient>());
        Assert.IsNotNull(provider.GetRequiredService<
            Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>>());
    }

    [TestMethod]
    public async Task RegisterAiChatClientAsync_ExplicitNone_RegistersNothing_LeavesFallbackToAddAiServices()
    {
        var builder = CreateHostBuilder(new Dictionary<string, string?>
        {
            [RegisterServices.AiProviderConfigKey] = "None",
            // Present but must be ignored: an explicit None short-circuits before the chat connection is read.
            ["ConnectionStrings:chat"] = "Endpoint=https://example.services.ai.azure.com/;Key=fake"
        });

        await builder.RegisterAiChatClientAsync(NullLogger.Instance, TestContext.CancellationToken);

        Assert.IsFalse(builder.Services.Any(d => d.ServiceType == typeof(Microsoft.Extensions.AI.IChatClient)));
        Assert.IsFalse(builder.Services.Any(d => d.ServiceType == typeof(AiProviderInfo)));
    }

    [TestMethod]
    public async Task RegisterAiChatClientAsync_ExplicitAzureInference_RecordsAzureProvider()
    {
        var builder = CreateHostBuilder(new Dictionary<string, string?>
        {
            [RegisterServices.AiProviderConfigKey] = "AzureInference",
            ["ConnectionStrings:chat"] = "Endpoint=https://example.services.ai.azure.com/;Key=fake"
        });

        await builder.RegisterAiChatClientAsync(NullLogger.Instance, TestContext.CancellationToken);

        var provider = builder.Services.BuildServiceProvider();
        Assert.AreEqual("azure", provider.GetRequiredService<AiProviderInfo>().Name);
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
