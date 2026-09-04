using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TaskFlow.Application.Contracts;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;
using Test.Support;
using Test.Support.Hosting;

namespace Test.E2E;

/// <summary>
/// Real-database WebApplicationFactory backed by a Testcontainer for the provider selected by
/// <c>TASKFLOW_TEST_DB_PROVIDER</c> (SQL Server default, PostgreSQL lane). Exercises the full stack:
/// HTTP -> endpoint style -> application layer -> EF -> database.
/// Set TASKFLOW_APPLICATION_STYLE=Cqrs to run the same workflow tests against CQRS endpoint mappings.
/// </summary>
public sealed class DbApiFactory : WebApplicationFactoryBase<Program, TaskFlowDbContextTrxn, TaskFlowDbContextQuery>
{
    private static readonly TestDatabaseContainer Db = new(TestDbProvider.Current);

    // The container is shared by every test class in this assembly and cannot be restarted once
    // disposed, so it is started on first use and torn down once from [AssemblyCleanup]. Disposing it
    // from a class cleanup pulled it out from under the classes that ran afterwards.
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static bool _started;

    private readonly string _applicationStyle;

    public static string? DockerUnavailableReason { get; private set; }

    public static Exception? StartupError { get; private set; }

    /// <summary>Initializes the API factory with required dependencies and default state.</summary>
    public DbApiFactory(string? applicationStyle = null)
    {
        _applicationStyle = applicationStyle
            ?? Environment.GetEnvironmentVariable(ApplicationStyleResolver.EnvironmentVariable)
            ?? ApplicationStyle.Service.ToString();
    }

    /// <summary>Starts the database container once per test run.</summary>
    public static async Task StartContainerAsync(CancellationToken cancellationToken)
    {
        await Gate.WaitAsync(cancellationToken);
        try
        {
            if (_started || DockerUnavailableReason is not null || StartupError is not null)
                return;

            DockerUnavailableReason = await DockerRuntimePreflight.GetUnavailableReasonAsync(
                TimeSpan.FromSeconds(10),
                cancellationToken);
            if (DockerUnavailableReason is not null)
                return;

            try
            {
                await Db.StartAsync();
                _started = true;
            }
            catch (Exception ex)
            {
                StartupError = ex;
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Stops the shared container. Called once per assembly, never from a class cleanup.</summary>
    public static async Task StopContainerAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (!_started) return;

            await Db.DisposeAsync();
            _started = false;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>Points the host at the container and selects the lane's provider (Database:Provider).</summary>
    protected override void ConfigureTestConfiguration(IConfigurationBuilder config)
    {
        AddFoundryLocalDisabled(config);
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ApplicationStyleResolver.ConfigKey] = _applicationStyle,
            [TaskFlowDbProviderSelector.ConfigurationKey] = Db.Provider.ToString()
        });
        config.AddInMemoryCollection(TestColumnEncryption.Configuration);
    }

    /// <summary>Builds trxn options used by focused test cases.</summary>
    protected override DbContextOptions BuildTrxnOptions() =>
        Db.BuildOptions<TaskFlowDbContextTrxn>(null, TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName);

    /// <summary>Builds query options used by focused test cases.</summary>
    protected override DbContextOptions BuildQueryOptions() =>
        Db.BuildOptions<TaskFlowDbContextQuery>(null, TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName);
}
