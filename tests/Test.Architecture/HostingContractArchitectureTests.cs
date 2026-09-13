namespace Test.Architecture;

[TestClass]
[TestCategory("Architecture")]
public sealed class HostingContractArchitectureTests
{
    [TestMethod]
    public void Given_SharedHostingProject_When_ProjectFileRead_Then_HasNoTaskFlowProjectDependency()
    {
        var project = File.ReadAllText(RepoFiles.Path(
            "src", "Shared", "TaskFlow.Hosting", "TaskFlow.Hosting.csproj"));

        Assert.IsFalse(project.Contains("ProjectReference", StringComparison.Ordinal));
    }

    [TestMethod]
    [DataRow("src", "Host", "Aspire", "AppHost", "AppHost.csproj")]
    [DataRow("src", "Host", "TaskFlow.Bootstrapper", "TaskFlow.Bootstrapper.csproj")]
    [DataRow("src", "Host", "TaskFlow.Gateway", "TaskFlow.Gateway.csproj")]
    [DataRow("src", "Infrastructure", "TaskFlow.Infrastructure.Data", "TaskFlow.Infrastructure.Data.csproj")]
    [DataRow("src", "Infrastructure", "TaskFlow.Infrastructure.AI", "TaskFlow.Infrastructure.AI.csproj")]
    [DataRow("tests", "Test.Support", "Test.Support.csproj")]
    public void Given_LaneContractConsumer_When_ProjectFileRead_Then_ReferencesSharedHostingProject(
        params string[] path)
    {
        var project = File.ReadAllText(RepoFiles.Path(path));

        StringAssert.Contains(project, "TaskFlow.Hosting.csproj");
    }
}
