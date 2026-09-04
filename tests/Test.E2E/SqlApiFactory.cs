using EF.IntegrationTesting.Testcontainers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TaskFlow.Application.Contracts;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;
using Test.Support;
using Test.Support.Hosting;

namespace Test.E2E;

/// <summary>
/// Real-SQL-Server WebApplicationFactory backed by Testcontainers.
/// Exercises the full stack: HTTP -> endpoint style -> application layer -> EF -> SQL.
/// Set TASKFLOW_APPLICATION_STYLE=Cqrs to run the same workflow tests against CQRS endpoint mappings.
/// </summary>
public sealed class SqlApiFactory : WebApplicationFactoryBase<Program, TaskFlowDbContextTrxn, TaskFlowDbContextQuery>
{
    private static readonly MsSqlContainerFixture Sql = new();
    private static bool _started;

    private readonly string _applicationStyle;

    public static string? DockerUnavailableReason { get; private set; }

    public static Exception? StartupError { get; private set; }

    /// <summary>Initializes SQL API factory with required dependencies and default state.</summary>
    public SqlApiFactory(string? applicationStyle = null)
    {
        _applicationStyle = applicationStyle
            ?? Environment.GetEnvironmentVariable(ApplicationStyleResolver.EnvironmentVariable)
            ?? ApplicationStyle.Service.ToString();
    }

    /// <summary>Verifies start container behavior and protects the expected test contract.</summary>
    public static async Task StartContainerAsync(CancellationToken cancellationToken)
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
            await Sql.StartAsync();
            _started = true;
        }
        catch (Exception ex)
        {
            StartupError = ex;
        }
    }

    /// <summary>Verifies stop container behavior and protects the expected test contract.</summary>
    public static async Task StopContainerAsync()
    {
        if (!_started)
            return;

        await Sql.DisposeAsync();
        _started = false;
    }

    /// <summary>Verifies configure test configuration behavior and protects the expected test contract.</summary>
    protected override void ConfigureTestConfiguration(IConfigurationBuilder config)
    {
        AddFoundryLocalDisabled(config);
        config.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [ApplicationStyleResolver.ConfigKey] = _applicationStyle
        });
    }

    /// <summary>Builds trxn options used by focused test cases.</summary>
    protected override DbContextOptions BuildTrxnOptions() =>
        BuildSqlServerOptions<TaskFlowDbContextTrxn>(Sql.ConnectionString);

    /// <summary>Builds query options used by focused test cases.</summary>
    protected override DbContextOptions BuildQueryOptions() =>
        BuildSqlServerOptions<TaskFlowDbContextQuery>(Sql.ConnectionString);

    /// <summary>Builds provider options used by focused test cases.</summary>
    private static DbContextOptions<TContext> BuildSqlServerOptions<TContext>(string connectionString)
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseTaskFlowProvider(new TaskFlowProviderOptions(
                TaskFlowDbProvider.SqlServer,
                connectionString,
                TaskFlowDbContextBase.MigrationHistoryTable,
                TaskFlowDbContextBase.SchemaName))
            .Options;
}
