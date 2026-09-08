using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;
using TaskFlow.Application.Contracts.Configuration;

namespace TaskFlow.Infrastructure.Data.Provider;

/// <summary>Relational providers supported by every TaskFlow DbContext and the migrator (D-020).</summary>
public enum TaskFlowDbProvider
{
    SqlServer,
    PostgreSql
}

/// <summary>
/// Everything a context needs to open a provider connection. Built once per registration site from
/// configuration so the SQL Server / PostgreSQL choice is made in exactly one place (D-030).
/// </summary>
public sealed record TaskFlowProviderOptions(
    TaskFlowDbProvider Provider,
    string ConnectionString,
    string MigrationsHistoryTable,
    string MigrationsHistorySchema,
    int MaxRetryCount = 5,
    int MaxRetryDelaySeconds = 30,
    int? CommandTimeoutSeconds = null,
    int CompatibilityLevel = TaskFlowProviderOptions.DefaultCompatibilityLevel,
    PoolerMode PoolerMode = PoolerMode.None)
{
    public const int DefaultCompatibilityLevel = 170;

    /// <summary>
    /// Resolves provider, retry, SQL Server compatibility, and PostgreSQL pooler settings from configuration
    /// (<c>Database:Retry:*</c>, <c>Database:SqlServer:CompatibilityLevel</c>, <c>Database:PostgreSql:PoolerMode</c>).
    /// </summary>
    public static TaskFlowProviderOptions FromConfiguration(
        IConfiguration configuration,
        string connectionString,
        string migrationsHistoryTable,
        string migrationsHistorySchema,
        int? commandTimeoutSeconds = null) =>
        new(
            TaskFlowDbProviderSelector.Resolve(configuration),
            connectionString,
            migrationsHistoryTable,
            migrationsHistorySchema,
            configuration.GetValue<int?>("Database:Retry:MaxRetryCount") ?? 5,
            configuration.GetValue<int?>("Database:Retry:MaxRetryDelaySeconds") ?? 30,
            commandTimeoutSeconds,
            configuration.GetValue<int?>("Database:SqlServer:CompatibilityLevel") ?? DefaultCompatibilityLevel,
            PoolerModeSelector.Resolve(configuration));
}

/// <summary>PgBouncer pooling mode the PostgreSQL connection string must cooperate with (D-045).</summary>
public enum PoolerMode
{
    /// <summary>No pooler in front of PostgreSQL (Azure default; Portable's compose profile opts in explicitly).</summary>
    None,

    /// <summary>PgBouncer transaction-mode pooling: connections are multiplexed across backend sessions.</summary>
    Transaction
}

/// <summary>Resolves <see cref="Provider.PoolerMode"/> from <c>Database:PostgreSql:PoolerMode</c>; no env override (D-045).</summary>
public static class PoolerModeSelector
{
    public const string ConfigurationKey = "Database:PostgreSql:PoolerMode";

    public static PoolerMode Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = configuration[ConfigurationKey];
        return string.IsNullOrWhiteSpace(value)
            ? PoolerMode.None
            : Enum.TryParse<PoolerMode>(value, ignoreCase: true, out var mode)
                ? mode
                : throw new ArgumentException(
                    $"Unknown pooler mode '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<PoolerMode>())}.");
    }
}

/// <summary>Resolves the active provider: env <c>TASKFLOW_DB_PROVIDER</c> wins over <c>Database:Provider</c>, default SqlServer.</summary>
public static class TaskFlowDbProviderSelector
{
    public const string EnvironmentVariable = "TASKFLOW_DB_PROVIDER";
    public const string ConfigurationKey = "Database:Provider";
    public const string SqlServerMigrationsAssembly = "TaskFlow.Infrastructure.Data.Migrations.SqlServer";
    public const string PostgreSqlMigrationsAssembly = "TaskFlow.Infrastructure.Data.Migrations.PostgreSql";

    public static TaskFlowDbProvider Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable) ?? configuration[ConfigurationKey];
        return string.IsNullOrWhiteSpace(value)
            ? LaneDefault(HostingLaneSelector.Resolve(configuration))
            : Parse(value);
    }

    public static TaskFlowDbProvider ResolveFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(EnvironmentVariable);
        return string.IsNullOrWhiteSpace(value)
            ? LaneDefault(HostingLaneSelector.ResolveFromEnvironment())
            : Parse(value);
    }

    public static string MigrationsAssembly(TaskFlowDbProvider provider) => provider switch
    {
        TaskFlowDbProvider.SqlServer => SqlServerMigrationsAssembly,
        TaskFlowDbProvider.PostgreSql => PostgreSqlMigrationsAssembly,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    // D-035: Portable lane defaults to PostgreSQL; Azure lane keeps today's SQL Server default.
    private static TaskFlowDbProvider LaneDefault(HostingLane lane) =>
        lane == HostingLane.Portable ? TaskFlowDbProvider.PostgreSql : TaskFlowDbProvider.SqlServer;

    private static TaskFlowDbProvider Parse(string value) =>
        Enum.TryParse<TaskFlowDbProvider>(value, ignoreCase: true, out var provider)
            ? provider
            : throw new ArgumentException(
                $"Unknown database provider '{value}'. Allowed values: {string.Join(", ", Enum.GetNames<TaskFlowDbProvider>())}.");
}

/// <summary>
/// The only runtime provider branch in the solution (D-030). Every DbContext registration, design-time
/// factory, test fixture, and the migrator go through this call.
/// </summary>
public static class TaskFlowDbProviderExtensions
{
    public static DbContextOptionsBuilder<TContext> UseTaskFlowProvider<TContext>(
        this DbContextOptionsBuilder<TContext> options,
        TaskFlowProviderOptions providerOptions)
        where TContext : DbContext =>
        (DbContextOptionsBuilder<TContext>)UseTaskFlowProvider((DbContextOptionsBuilder)options, providerOptions);

    public static DbContextOptionsBuilder UseTaskFlowProvider(
        this DbContextOptionsBuilder options,
        TaskFlowProviderOptions providerOptions)
    {
        var retryDelay = TimeSpan.FromSeconds(providerOptions.MaxRetryDelaySeconds);
        var migrationsAssembly = TaskFlowDbProviderSelector.MigrationsAssembly(providerOptions.Provider);

        switch (providerOptions.Provider)
        {
            case TaskFlowDbProvider.SqlServer:
                // Azure SQL gets the Azure-tuned execution strategy; everything else (container, on-prem)
                // uses the generic SQL Server provider. Both share the same compatibility level and history table.
                if (providerOptions.ConnectionString.Contains("database.windows.net", StringComparison.OrdinalIgnoreCase))
                {
                    options.UseAzureSql(providerOptions.ConnectionString, sql =>
                    {
                        sql.UseCompatibilityLevel(providerOptions.CompatibilityLevel);
                        sql.EnableRetryOnFailure(providerOptions.MaxRetryCount, retryDelay, errorNumbersToAdd: null);
                        sql.MigrationsHistoryTable(providerOptions.MigrationsHistoryTable, providerOptions.MigrationsHistorySchema);
                        sql.MigrationsAssembly(migrationsAssembly);
                        if (providerOptions.CommandTimeoutSeconds is int timeout) sql.CommandTimeout(timeout);
                    });
                }
                else
                {
                    options.UseSqlServer(providerOptions.ConnectionString, sql =>
                    {
                        sql.UseCompatibilityLevel(providerOptions.CompatibilityLevel);
                        sql.EnableRetryOnFailure(providerOptions.MaxRetryCount, retryDelay, errorNumbersToAdd: null);
                        sql.MigrationsHistoryTable(providerOptions.MigrationsHistoryTable, providerOptions.MigrationsHistorySchema);
                        sql.MigrationsAssembly(migrationsAssembly);
                        if (providerOptions.CommandTimeoutSeconds is int timeout) sql.CommandTimeout(timeout);
                    });
                }
                break;

            case TaskFlowDbProvider.PostgreSql:
                var npgsqlConnectionString = providerOptions.PoolerMode == PoolerMode.Transaction
                    ? AppendTransactionPoolerFlags(providerOptions.ConnectionString)
                    : providerOptions.ConnectionString;
                options.UseNpgsql(npgsqlConnectionString, npgsql =>
                {
                    npgsql.EnableRetryOnFailure(providerOptions.MaxRetryCount, retryDelay, errorCodesToAdd: null);
                    npgsql.MigrationsHistoryTable(providerOptions.MigrationsHistoryTable, providerOptions.MigrationsHistorySchema);
                    npgsql.MigrationsAssembly(migrationsAssembly);
                    if (providerOptions.CommandTimeoutSeconds is int timeout) npgsql.CommandTimeout(timeout);
                });
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(providerOptions), providerOptions.Provider, null);
        }

        return options;
    }

    // D-045: transaction-mode PgBouncer multiplexes one backend connection across many client sessions, so
    // Npgsql must not reset session state on return to the pool or rely on server-side prepared statements
    // surviving between calls - both assume a stable backend connection that transaction pooling breaks.
    private static string AppendTransactionPoolerFlags(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString)
        {
            NoResetOnClose = true,
            MaxAutoPrepare = 0
        }.ConnectionString;
}
