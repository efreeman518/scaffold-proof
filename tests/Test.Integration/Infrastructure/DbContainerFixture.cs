using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;
using Test.Support.Hosting;

namespace Test.Integration.Infrastructure;

/// <summary>
/// Standalone database Testcontainer for the component tier, SQL Server or PostgreSQL depending on
/// <c>TASKFLOW_TEST_DB_PROVIDER</c> (the same assembly runs once per lane). Started once by
/// <see cref="IntegrationTestSetup"/>; <see cref="StartupError"/> is captured so dependent tests fail with
/// its diagnostics without aborting assembly discovery.
/// </summary>
internal static class DbContainerFixture
{
    private static readonly TestDatabaseContainer Container = new(TestDbProvider.Current);

    /// <summary>The provider this lane runs against.</summary>
    internal static TaskFlowDbProvider Provider => Container.Provider;

    /// <summary>Startup failure captured by <see cref="StartAsync"/>; null when the container started cleanly.</summary>
    internal static Exception? StartupError { get; private set; }

    /// <summary>Connection string for the running container. Only valid once startup succeeded.</summary>
    internal static string ConnectionString => Container.ConnectionString;

    /// <summary>Starts the container, capturing any post-preflight failure for dependent tests.</summary>
    internal static async Task StartAsync()
    {
        try
        {
            await Container.StartAsync();
        }
        catch (Exception ex)
        {
            StartupError = ex;
        }
    }

    /// <summary>Disposes the container.</summary>
    internal static async Task StopAsync() => await Container.DisposeAsync();

    /// <summary>Creates an empty isolated database and returns a connection string pointing to it.</summary>
    internal static Task<string> CreateEmptyDatabaseConnectionStringAsync(string prefix) =>
        Container.CreateEmptyDatabaseAsync(prefix);

    /// <summary>Builds a trxn context against the container.</summary>
    internal static TaskFlowDbContextTrxn CreateTrxnContext(string? connString = null) =>
        new(Container.BuildOptions<TaskFlowDbContextTrxn>(connString, TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName))
        { AuditId = "integration-test" };

    /// <summary>
    /// Builds a query context against the container with extra interceptors attached (a command counter, for
    /// instance). Interceptors can only be supplied at options-build time, never on a live context.
    /// </summary>
    internal static TaskFlowDbContextQuery CreateQueryContext(
        string? connString, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
        new(Container.BuildOptions<TaskFlowDbContextQuery>(
            connString, TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName, interceptors))
        { AuditId = "integration-test" };

    /// <summary>Builds a query context against the container.</summary>
    internal static TaskFlowDbContextQuery CreateQueryContext(string? connString = null) =>
        new(Container.BuildOptions<TaskFlowDbContextQuery>(connString, TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName))
        { AuditId = "integration-test" };

    /// <summary>Builds a FlowEngine context against the container.</summary>
    internal static TaskFlowFlowEngineDbContext CreateFlowEngineContext(string? connString = null) =>
        new(Container.BuildOptions<TaskFlowFlowEngineDbContext>(connString, TaskFlowFlowEngineDbContext.MigrationHistoryTable, TaskFlowFlowEngineDbContext.SchemaName));

    /// <summary>Builds a TickerQ context against the container.</summary>
    internal static TaskFlowTickerQDbContext CreateTickerQContext(string? connString = null) =>
        new(Container.BuildOptions<TaskFlowTickerQDbContext>(connString, TaskFlowTickerQDbContext.MigrationHistoryTable, TaskFlowTickerQDbContext.SchemaName));
}
