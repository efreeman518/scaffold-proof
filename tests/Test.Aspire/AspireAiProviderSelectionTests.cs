using EF.Testing.Environment;
using TaskFlow.Hosting;

namespace Test.Aspire;

/// <summary>
/// Fast checks for the Aspire test harness AI provider selection. It follows the shared lane resolver, the same
/// switch the AppHost and the hosts use: Foundry is selected only when the resolved provider is AzureInference.
/// </summary>
[TestClass]
[TestCategory("Foundry")]
[DoNotParallelize]
public sealed class AspireAiProviderSelectionTests
{
    private static readonly string[] AiConfigurationVariables =
    [
        HostingLaneResolver.LaneEnvironmentVariable,
        HostingLaneResolver.DatabaseEnvironmentVariable,
        HostingLaneResolver.MessagingEnvironmentVariable,
        HostingLaneResolver.StorageEnvironmentVariable,
        HostingLaneResolver.ReadModelEnvironmentVariable,
        HostingLaneResolver.AuditEnvironmentVariable,
        HostingLaneResolver.SearchEnvironmentVariable,
        HostingLaneResolver.AiEnvironmentVariable,
        HostingLaneResolver.DataProtectionEnvironmentVariable,
        "AiServices__Provider",
        "ConnectionStrings__chat",
        "AiServices__FoundryEndpoint",
        "AiServices__AgentModelDeployment",
    ];

    [TestMethod]
    public void Given_NoAzureConfig_When_SelectingLiveAiProvider_Then_NoProviderSelected()
    {
        using var _ = WithoutAiConfiguration();

        Assert.AreEqual(AspireAiProvider.None, AspireTestHost.SelectRequestedAiProviderForTesting());
    }

    /// <summary>Settings alone do not select Foundry; the AppHost rejects them without the provider.</summary>
    [TestMethod]
    [DataRow("AiServices__FoundryEndpoint", "https://taskflow.services.ai.azure.com/")]
    [DataRow("AiServices__AgentModelDeployment", "chat-deployment")]
    [DataRow("ConnectionStrings__chat", "Endpoint=https://taskflow.services.ai.azure.com/;Deployment=chat-deployment")]
    public void Given_FoundrySettingWithoutProvider_When_SelectingLiveAiProvider_Then_NoProviderSelected(
        string name, string value)
    {
        using var _ = WithoutAiConfiguration()
            .Set(HostingLaneResolver.LaneEnvironmentVariable, "Azure")
            .Set(name, value);

        Assert.AreEqual(AspireAiProvider.None, AspireTestHost.SelectRequestedAiProviderForTesting());
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    [DataRow(HostingLaneResolver.AiEnvironmentVariable)]
    [DataRow("AiServices__Provider")]
    public void Given_AzureInferenceProviderOnAzureLane_When_SelectingLiveAiProvider_Then_AzureFoundryWins(string name)
    {
        using var _ = WithoutAiConfiguration()
            .Set(HostingLaneResolver.LaneEnvironmentVariable, "Azure")
            .Set(name, "AzureInference");

        Assert.AreEqual(AspireAiProvider.AzureFoundry, AspireTestHost.SelectRequestedAiProviderForTesting());
    }

    // Clears every input the lane resolver reads, so only the test's own values count.
    // Later Set calls on the returned scope keep the original captured here and are restored on dispose.
    private static EnvironmentVariableScope WithoutAiConfiguration()
    {
        var environment = new EnvironmentVariableScope();
        foreach (var name in AiConfigurationVariables) environment.Set(name, null);
        return environment;
    }
}
