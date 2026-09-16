namespace Test.Aspire;

/// <summary>Fast checks for the Aspire test harness AI provider defaults.</summary>
[TestClass]
[TestCategory("Foundry")]
[DoNotParallelize]
public sealed class AspireAiProviderSelectionTests
{
    [TestMethod]
    public void Given_NoAzureConfig_When_SelectingLiveAiProvider_Then_NoProviderSelected()
    {
        using var _ = new EnvironmentOverride(
            ("TASKFLOW_AI_PROVIDER", null),
            ("AiServices__Provider", null),
            ("AiServices:Provider", null),
            ("TASKFLOW_USE_AZURE_FOUNDRY", null),
            ("ConnectionStrings__chat", null),
            ("ConnectionStrings:chat", null),
            ("AiServices__FoundryEndpoint", null),
            ("AiServices:FoundryEndpoint", null),
            ("AiServices__AgentModelDeployment", null),
            ("AiServices:AgentModelDeployment", null));

        Assert.AreEqual(AspireAiProvider.None, AspireTestHost.SelectRequestedAiProviderForTesting());
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public void Given_EndpointAndDeployment_When_SelectingLiveAiProvider_Then_AzureFoundryWins()
    {
        using var _ = new EnvironmentOverride(
            ("TASKFLOW_AI_PROVIDER", null),
            ("AiServices__Provider", null),
            ("AiServices:Provider", null),
            ("TASKFLOW_USE_AZURE_FOUNDRY", null),
            ("ConnectionStrings__chat", null),
            ("ConnectionStrings:chat", null),
            ("AiServices__FoundryEndpoint", "https://taskflow.services.ai.azure.com/"),
            ("AiServices:FoundryEndpoint", null),
            ("AiServices__AgentModelDeployment", "chat-deployment"),
            ("AiServices:AgentModelDeployment", null));

        Assert.AreEqual(AspireAiProvider.AzureFoundry, AspireTestHost.SelectRequestedAiProviderForTesting());
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public void Given_CompleteConnection_When_SelectingLiveAiProvider_Then_AzureFoundryWins()
    {
        using var _ = new EnvironmentOverride(
            ("TASKFLOW_AI_PROVIDER", null),
            ("AiServices__Provider", null),
            ("AiServices:Provider", null),
            ("TASKFLOW_USE_AZURE_FOUNDRY", null),
            ("ConnectionStrings__chat", "Endpoint=https://taskflow.services.ai.azure.com/;Deployment=chat-deployment"),
            ("ConnectionStrings:chat", null),
            ("AiServices__FoundryEndpoint", null),
            ("AiServices:FoundryEndpoint", null),
            ("AiServices__AgentModelDeployment", null),
            ("AiServices:AgentModelDeployment", null));

        Assert.AreEqual(AspireAiProvider.AzureFoundry, AspireTestHost.SelectRequestedAiProviderForTesting());
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public void Given_AzureInferenceEnvironmentProvider_When_SelectingLiveAiProvider_Then_AzureFoundryWins()
    {
        using var _ = new EnvironmentOverride(
            ("TASKFLOW_AI_PROVIDER", "AzureInference"),
            ("AiServices__Provider", null),
            ("AiServices:Provider", null),
            ("TASKFLOW_USE_AZURE_FOUNDRY", null),
            ("ConnectionStrings__chat", null),
            ("ConnectionStrings:chat", null),
            ("AiServices__FoundryEndpoint", null),
            ("AiServices:FoundryEndpoint", null),
            ("AiServices__AgentModelDeployment", null),
            ("AiServices:AgentModelDeployment", null));

        Assert.AreEqual(AspireAiProvider.AzureFoundry, AspireTestHost.SelectRequestedAiProviderForTesting());
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public void Given_AzureInferenceConfigurationProvider_When_SelectingLiveAiProvider_Then_AzureFoundryWins()
    {
        using var _ = new EnvironmentOverride(
            ("TASKFLOW_AI_PROVIDER", null),
            ("AiServices__Provider", "AzureInference"),
            ("AiServices:Provider", null),
            ("TASKFLOW_USE_AZURE_FOUNDRY", null),
            ("ConnectionStrings__chat", null),
            ("ConnectionStrings:chat", null),
            ("AiServices__FoundryEndpoint", null),
            ("AiServices:FoundryEndpoint", null),
            ("AiServices__AgentModelDeployment", null),
            ("AiServices:AgentModelDeployment", null));

        Assert.AreEqual(AspireAiProvider.AzureFoundry, AspireTestHost.SelectRequestedAiProviderForTesting());
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public void Given_DeploymentOnly_When_SelectingLiveAiProvider_Then_AzureFoundryWins()
    {
        using var _ = new EnvironmentOverride(
            ("TASKFLOW_AI_PROVIDER", null),
            ("AiServices__Provider", null),
            ("AiServices:Provider", null),
            ("TASKFLOW_USE_AZURE_FOUNDRY", null),
            ("ConnectionStrings__chat", null),
            ("ConnectionStrings:chat", null),
            ("AiServices__FoundryEndpoint", null),
            ("AiServices:FoundryEndpoint", null),
            ("AiServices__AgentModelDeployment", "chat-deployment"),
            ("AiServices:AgentModelDeployment", null));

        Assert.AreEqual(AspireAiProvider.AzureFoundry, AspireTestHost.SelectRequestedAiProviderForTesting());
    }

    private sealed class EnvironmentOverride : IDisposable
    {
        private readonly Dictionary<string, string?> _originalValues = new(StringComparer.Ordinal);

        public EnvironmentOverride(params (string Name, string? Value)[] values)
        {
            foreach (var (name, value) in values)
            {
                _originalValues[name] = Environment.GetEnvironmentVariable(name);
                Environment.SetEnvironmentVariable(name, value);
            }
        }

        public void Dispose()
        {
            foreach (var (name, value) in _originalValues)
                Environment.SetEnvironmentVariable(name, value);
        }
    }
}
