namespace Test.Integration.Infrastructure;

using Test.Support.Hosting;

/// <summary>
/// Assembly-scoped lifecycle for the component tier. Starts the standalone database (SQL Server or PostgreSQL, TASKFLOW_TEST_DB_PROVIDER) and Azurite
/// Testcontainers in parallel via <c>[AssemblyInitialize]</c> and disposes them via
/// <c>[AssemblyCleanup]</c>. A bounded Docker preflight is the only inconclusive path. Each fixture captures
/// its own <c>StartupError</c> so dependent tests fail with diagnostics without aborting assembly discovery.
/// Component tier only - no Aspire graph, no <c>AppHost</c> reference.
/// </summary>
[TestClass]
public static class IntegrationTestSetup
{
    internal static string? DockerUnavailableReason { get; private set; }

    /// <summary>Starts the component-tier store containers in parallel before any test runs.</summary>
    [AssemblyInitialize]
    public static async Task AssemblyInit(TestContext context)
    {
        DockerUnavailableReason = await DockerRuntimePreflight.GetUnavailableReasonAsync(
            TimeSpan.FromSeconds(10),
            context.CancellationToken);
        if (DockerUnavailableReason is not null)
            return;

        await Task.WhenAll(
            DbContainerFixture.StartAsync(),
            AzuriteContainerFixture.StartAsync());
    }

    /// <summary>Disposes the component-tier store containers after the assembly's tests complete.</summary>
    [AssemblyCleanup]
    public static async Task AssemblyCleanup(TestContext _)
    {
        if (DockerUnavailableReason is null)
        {
            await Task.WhenAll(
                DbContainerFixture.StopAsync(),
                AzuriteContainerFixture.StopAsync(),
                // Started lazily by the D-034 transport tests; a no-op when they did not run.
                RabbitMqBrokerFixture.StopAsync());
        }
    }

    internal static bool IsUnavailable(Exception? startupError) =>
        DockerUnavailableReason is not null || startupError is not null;

    internal static void AssertAvailable(string resourceName, Exception? startupError)
    {
        if (DockerUnavailableReason is not null)
        {
            Assert.Inconclusive(DockerUnavailableReason);
            return;
        }

        if (startupError is not null)
            Assert.Fail($"{resourceName} container startup failed after Docker preflight succeeded:{Environment.NewLine}{startupError}");
    }
}
