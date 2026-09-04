using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using System.Diagnostics.CodeAnalysis;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;

namespace TaskFlow.DatabaseMigrator;

// EF tooling entry points. They live in the migrator (the --startup-project) so one factory set serves
// both provider migration assemblies: `TASKFLOW_DB_PROVIDER` selects the provider, `EFCORETOOLSDB*`
// supplies a (dummy for `migrations add`) connection string.

/// <summary>Creates the transactional context for EF tooling.</summary>
[ExcludeFromCodeCoverage]
public sealed class DesignTimeDbContextFactoryTrxn : IDesignTimeDbContextFactory<TaskFlowDbContextTrxn>
{
    public TaskFlowDbContextTrxn CreateDbContext(string[] args) =>
        new(DesignTimeOptions.Build<TaskFlowDbContextTrxn>(
            "EFCORETOOLSDB",
            TaskFlowDbContextBase.MigrationHistoryTable,
            TaskFlowDbContextBase.SchemaName))
        {
            AuditId = "DesignTimeAuditId",
            TenantId = Guid.NewGuid()
        };
}

/// <summary>Creates the query context for EF tooling.</summary>
[ExcludeFromCodeCoverage]
public sealed class DesignTimeDbContextFactoryQuery : IDesignTimeDbContextFactory<TaskFlowDbContextQuery>
{
    public TaskFlowDbContextQuery CreateDbContext(string[] args) =>
        new(DesignTimeOptions.Build<TaskFlowDbContextQuery>(
            "EFCORETOOLSDB",
            TaskFlowDbContextBase.MigrationHistoryTable,
            TaskFlowDbContextBase.SchemaName))
        {
            AuditId = "DesignTimeAuditId",
            TenantId = Guid.NewGuid()
        };
}

/// <summary>Creates the FlowEngine context for EF tooling.</summary>
[ExcludeFromCodeCoverage]
public sealed class DesignTimeDbContextFactoryFlowEngine : IDesignTimeDbContextFactory<TaskFlowFlowEngineDbContext>
{
    public TaskFlowFlowEngineDbContext CreateDbContext(string[] args) =>
        new(DesignTimeOptions.Build<TaskFlowFlowEngineDbContext>(
            "EFCORETOOLSDB_FLOWENGINE",
            TaskFlowFlowEngineDbContext.MigrationHistoryTable,
            TaskFlowFlowEngineDbContext.SchemaName));
}

/// <summary>Creates the TickerQ context for EF tooling.</summary>
[ExcludeFromCodeCoverage]
public sealed class DesignTimeDbContextFactoryTickerQ : IDesignTimeDbContextFactory<TaskFlowTickerQDbContext>
{
    public TaskFlowTickerQDbContext CreateDbContext(string[] args) =>
        new(DesignTimeOptions.Build<TaskFlowTickerQDbContext>(
            "EFCORETOOLSDB_TICKERQ",
            TaskFlowTickerQDbContext.MigrationHistoryTable,
            TaskFlowTickerQDbContext.SchemaName));
}

file static class DesignTimeOptions
{
    public static DbContextOptions<TContext> Build<TContext>(
        string connectionStringVariable,
        string migrationsHistoryTable,
        string migrationsHistorySchema)
        where TContext : DbContext
    {
        var connectionString = Environment.GetEnvironmentVariable(connectionStringVariable)
            ?? Environment.GetEnvironmentVariable("EFCORETOOLSDB")
            ?? throw new InvalidOperationException(
                $"The connection string was not set in '{connectionStringVariable}' or 'EFCORETOOLSDB' environment variable.");

        var builder = new DbContextOptionsBuilder<TContext>();
        builder.UseTaskFlowProvider(new TaskFlowProviderOptions(
            TaskFlowDbProviderSelector.ResolveFromEnvironment(),
            connectionString,
            migrationsHistoryTable,
            migrationsHistorySchema));
        return builder.Options;
    }
}
