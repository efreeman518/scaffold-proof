using EF.Testing.Environment;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// Verifies the FlowEngine workflow host leaves the process environment as it found it. The factory pushes
/// its overrides through process-wide environment variables, so a dispose that clears instead of restoring
/// would erase a developer's or CI job's own values for every later test in the run.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class FlowEngineWorkflowApiFactoryTests
{
    [TestMethod]
    public void Given_ShellEnvironment_When_FactoryIsDisposed_Then_OriginalValuesAreRestored()
    {
        // The strict-lane overrides read the lane's container endpoints, so the containers must be up.
        IntegrationTestSetup.AssertAvailable("Redis", RedisContainerFixture.StartupError);
        using var shell = new EnvironmentVariableScope()
            .Set("ASPNETCORE_ENVIRONMENT", "ShellEnvironment")
            .Set("ConnectionStrings__chat", "ShellChatConnection")
            .Set("FlowEngine__TaskFlowApiBaseUrl", null);

        new FlowEngineWorkflowApiFactory("Server=unused", _ => string.Empty).Dispose();

        Assert.AreEqual("ShellEnvironment", Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT"));
        Assert.AreEqual("ShellChatConnection", Environment.GetEnvironmentVariable("ConnectionStrings__chat"));
        Assert.IsNull(Environment.GetEnvironmentVariable("FlowEngine__TaskFlowApiBaseUrl"));
    }
}
