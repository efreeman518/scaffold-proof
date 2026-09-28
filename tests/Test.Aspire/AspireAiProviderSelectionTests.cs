using EF.IntegrationTesting.Environment;

namespace Test.Aspire;

/// <summary>Fast checks for the Aspire test harness AI provider defaults.</summary>
[TestClass]
[TestCategory("Foundry")]
[DoNotParallelize]
public sealed class AspireAiProviderSelectionTests
{
    private static readonly string[] AiConfigurationVariables =
    [
        "TASKFLOW_AI_PROVIDER",
        "AiServices__Provider",
        "AiServices:Provider",
        "TASKFLOW_USE_AZURE_FOUNDRY",
        "ConnectionStrings__chat",
        "ConnectionStrings:chat",
        "AiServices__FoundryEndpoint",
        "AiServices:FoundryEndpoint",
        "AiServices__AgentModelDeployment",
        "AiServices:AgentModelDeployment",
    ];

    [TestMethod]
    public void Given_NoAzureConfig_When_SelectingLiveAiProvider_Then_NoProviderSelected()
    {
        using var _ = WithoutAiConfiguration();

        Assert.AreEqual(AspireAiProvider.None, AspireTestHost.SelectRequestedAiProviderForTesting());
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public void Given_EndpointAndDeployment_When_SelectingLiveAiProvider_Then_AzureFoundryWins()
    {
        using var _ = WithoutAiConfiguration()
            .Set("AiServices__FoundryEndpoint", "https://taskflow.services.ai.azure.com/")
            .Set("AiServices__AgentModelDeployment", "chat-deployment");

        Assert.AreEqual(AspireAiProvider.AzureFoundry, AspireTestHost.SelectRequestedAiProviderForTesting());
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public void Given_CompleteConnection_When_SelectingLiveAiProvider_Then_AzureFoundryWins()
    {
        using var _ = WithoutAiConfiguration()
            .Set("ConnectionStrings__chat", "Endpoint=https://taskflow.services.ai.azure.com/;Deployment=chat-deployment");

        Assert.AreEqual(AspireAiProvider.AzureFoundry, AspireTestHost.SelectRequestedAiProviderForTesting());
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public void Given_AzureInferenceEnvironmentProvider_When_SelectingLiveAiProvider_Then_AzureFoundryWins()
    {
        using var _ = WithoutAiConfiguration()
            .Set("TASKFLOW_AI_PROVIDER", "AzureInference");

        Assert.AreEqual(AspireAiProvider.AzureFoundry, AspireTestHost.SelectRequestedAiProviderForTesting());
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public void Given_AzureInferenceConfigurationProvider_When_SelectingLiveAiProvider_Then_AzureFoundryWins()
    {
        using var _ = WithoutAiConfiguration()
            .Set("AiServices__Provider", "AzureInference");

        Assert.AreEqual(AspireAiProvider.AzureFoundry, AspireTestHost.SelectRequestedAiProviderForTesting());
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public void Given_DeploymentOnly_When_SelectingLiveAiProvider_Then_AzureFoundryWins()
    {
        using var _ = WithoutAiConfiguration()
            .Set("AiServices__AgentModelDeployment", "chat-deployment");

        Assert.AreEqual(AspireAiProvider.AzureFoundry, AspireTestHost.SelectRequestedAiProviderForTesting());
    }

    // Clears every input SelectRequestedAiProviderForTesting reads, so only the test's own values count.
    // Later Set calls on the returned scope keep the original captured here and are restored on dispose.
    private static EnvironmentVariableScope WithoutAiConfiguration()
    {
        var environment = new EnvironmentVariableScope();
        foreach (var name in AiConfigurationVariables) environment.Set(name, null);
        return environment;
    }
}
