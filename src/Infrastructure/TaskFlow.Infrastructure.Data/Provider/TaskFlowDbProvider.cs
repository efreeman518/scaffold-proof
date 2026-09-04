using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

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
    int CompatibilityLevel = TaskFlowProviderOptions.DefaultCompatibilityLevel)
{
    public const int DefaultCompatibilityLevel = 170;

    /// <summary>
    /// Resolves provider, retry, and SQL Server compatibility settings from configuration
    /// (<c>Database:Retry:*</c>, <c>Database:SqlServer:CompatibilityLevel</c>).
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
            configuration.GetValue<int?>("Database:SqlServer:CompatibilityLevel") ?? DefaultCompatibilityLevel);
}

/// <summary>Resolves the active provider: env <c>TASKFLOW_DB_PROVIDER</c> wins over <c>Database:Provider</c>, default SqlServer.</summary>
public static class TaskFlowDbProviderSelector
{
    public const string EnvironmentVariable = "TASKFLOW_DB_PROVIDER";
    public const string ConfigurationKey = "Database:Provider";
    public const string SqlServerMigrationsAssembly = "TaskFlow.Infrastructure.Data.Migrations.SqlServer";
    public const string PostgreSqlMigrationsAssembly = "TaskFlow.Infrastructure.Data.Migrations.PostgreSql";

    public static TaskFlowDbProvider Resolve(IConfiguration configuration) =>
        Parse(Environment.GetEnvironmentVariable(EnvironmentVariable) ?? configuration[ConfigurationKey]);

    public static TaskFlowDbProvider ResolveFromEnvironment() =>
        Parse(Environment.GetEnvironmentVariable(EnvironmentVariable));

    public static string MigrationsAssembly(TaskFlowDbProvider provider) => provider switch
    {
        TaskFlowDbProvider.SqlServer => SqlServerMigrationsAssembly,
        TaskFlowDbProvider.PostgreSql => PostgreSqlMigrationsAssembly,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    private static TaskFlowDbProvider Parse(string? value) =>
        string.IsNullOrWhiteSpace(value)
            ? TaskFlowDbProvider.SqlServer
            : Enum.Parse<TaskFlowDbProvider>(value, ignoreCase: true);
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
                options.UseNpgsql(providerOptions.ConnectionString, npgsql =>
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
}
