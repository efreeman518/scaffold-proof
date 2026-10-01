using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;
using Test.Support.Hosting;

namespace Test.Integration.Infrastructure;

/// <summary>
/// Standalone database Testcontainer for the component tier, SQL Server or PostgreSQL depending on
/// <c>TASKFLOW_LANE</c> (the same assembly runs once per lane). Started once by
/// <see cref="IntegrationTestSetup"/>; <see cref="StartupError"/> is captured so dependent tests fail with
/// its diagnostics without aborting assembly discovery.
/// </summary>
internal static class DbContainerFixture
{
    private static readonly TestDatabaseContainer Container = new(TestHostingLane.DatabaseProvider);

    /// <summary>The provider this lane runs against.</summary>
    internal static TaskFlowDbProvider Provider => Container.Provider;

    /// <summary>Startup failure recorded by the fixture; null when the container started cleanly.</summary>
    internal static Exception? StartupError => Container.StartupError;

    /// <summary>Connection string for the running container. Only valid once startup succeeded.</summary>
    internal static string ConnectionString => Container.ConnectionString;

    /// <summary>Starts the container; a post-preflight failure is kept in <see cref="StartupError"/> for dependent tests.</summary>
    internal static Task StartAsync(CancellationToken cancellationToken = default) => Container.StartAsync(cancellationToken);

    /// <summary>Disposes the container.</summary>
    internal static Task StopAsync() => Container.DisposeAsync().AsTask();

    /// <summary>Creates an empty isolated database and returns a connection string pointing to it.</summary>
    internal static Task<string> CreateEmptyDatabaseConnectionStringAsync(string prefix, CancellationToken cancellationToken) =>
        Container.CreateEmptyDatabaseAsync(prefix, cancellationToken);

    // The component contexts carry no tenant, and EF.Data's tenant query filter reads nothing for a tenant-less
    // context unless it is marked all-tenants; a test that pins a tenant sets TenantId and clears AllTenants.

    /// <summary>Builds a trxn context against the container.</summary>
    internal static TaskFlowDbContextTrxn CreateTrxnContext(string? connString = null) =>
        new(Container.BuildOptions<TaskFlowDbContextTrxn>(connString, TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName))
        { AuditId = "integration-test", AllTenants = true };

    /// <summary>Builds a trxn context against the container with extra interceptors attached (a staged race, for instance).</summary>
    internal static TaskFlowDbContextTrxn CreateTrxnContext(
        string? connString, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
        new(Container.BuildOptions<TaskFlowDbContextTrxn>(
            connString, TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName, interceptors))
        { AuditId = "integration-test", AllTenants = true };

    /// <summary>
    /// Builds a query context against the container with extra interceptors attached (a command counter, for
    /// instance). Interceptors can only be supplied at options-build time, never on a live context.
    /// </summary>
    internal static TaskFlowDbContextQuery CreateQueryContext(
        string? connString, params Microsoft.EntityFrameworkCore.Diagnostics.IInterceptor[] interceptors) =>
        new(Container.BuildOptions<TaskFlowDbContextQuery>(
            connString, TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName, interceptors))
        { AuditId = "integration-test", AllTenants = true };

    /// <summary>Builds a query context against the container.</summary>
    internal static TaskFlowDbContextQuery CreateQueryContext(string? connString = null) =>
        new(Container.BuildOptions<TaskFlowDbContextQuery>(connString, TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName))
        { AuditId = "integration-test", AllTenants = true };

    /// <summary>Builds a FlowEngine context against the container.</summary>
    internal static TaskFlowFlowEngineDbContext CreateFlowEngineContext(string? connString = null) =>
        new(Container.BuildOptions<TaskFlowFlowEngineDbContext>(connString, TaskFlowFlowEngineDbContext.MigrationHistoryTable, TaskFlowFlowEngineDbContext.SchemaName));

    /// <summary>Builds a TickerQ context against the container.</summary>
    internal static TaskFlowTickerQDbContext CreateTickerQContext(string? connString = null) =>
        new(Container.BuildOptions<TaskFlowTickerQDbContext>(connString, TaskFlowTickerQDbContext.MigrationHistoryTable, TaskFlowTickerQDbContext.SchemaName));
}
