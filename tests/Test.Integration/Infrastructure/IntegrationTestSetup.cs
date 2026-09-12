namespace Test.Integration.Infrastructure;

using TaskFlow.Hosting;
using Test.Support.Hosting;

/// <summary>
/// Assembly-scoped lifecycle for the component tier. Starts only the strict lane's resources in parallel:
/// SQL Server and Azurite for Azure; PostgreSQL, RabbitMQ, and SeaweedFS for NonAzure; Redis for both;
/// MongoDB only for the explicit NonAzure read-model selection.
/// <c>[AssemblyCleanup]</c>. A bounded Docker preflight is the only inconclusive path. Each fixture captures
/// its own <c>StartupError</c> so dependent tests fail with diagnostics without aborting assembly discovery.
/// Component tier only - no Aspire graph, no <c>AppHost</c> reference.
/// </summary>
[TestClass]
public static class IntegrationTestSetup
{
    private static HostingLane _lane;
    private static bool _usesMongoDb;

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

        var settings = TestHostingLane.Current;
        _lane = settings.Lane;
        _usesMongoDb = TestHostingLane.UsesMongoDb;
        var starts = new List<Task>
        {
            DbContainerFixture.StartAsync(),
            RedisContainerFixture.StartAsync()
        };

        if (_lane == HostingLane.Azure)
        {
            starts.Add(AzuriteContainerFixture.StartAsync());
        }
        else
        {
            starts.Add(SeaweedFsContainerFixture.StartAsync());
            starts.Add(RabbitMqBrokerFixture.EnsureStartedAsync(context.CancellationToken));
            if (_usesMongoDb) starts.Add(MongoDbContainerFixture.StartAsync());
        }

        await Task.WhenAll(starts);
    }

    /// <summary>Disposes the component-tier store containers after the assembly's tests complete.</summary>
    [AssemblyCleanup]
    public static async Task AssemblyCleanup(TestContext _)
    {
        if (DockerUnavailableReason is null)
        {
            var stops = new List<Task>
            {
                DbContainerFixture.StopAsync(),
                RedisContainerFixture.StopAsync()
            };
            if (_lane == HostingLane.Azure)
            {
                stops.Add(AzuriteContainerFixture.StopAsync());
            }
            else
            {
                stops.Add(SeaweedFsContainerFixture.StopAsync());
                stops.Add(RabbitMqBrokerFixture.StopAsync());
                if (_usesMongoDb) stops.Add(MongoDbContainerFixture.StopAsync());
            }

            await Task.WhenAll(stops);
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

    internal static void RequireLane(HostingLane lane, bool requireMongoDb = false)
    {
        if (_lane != lane)
            Assert.Inconclusive($"Test requires the {lane} lane; current lane is {_lane}.");
        if (requireMongoDb && !_usesMongoDb)
            Assert.Inconclusive("Test requires TASKFLOW_READMODEL_PROVIDER=MongoDb.");
    }
}
