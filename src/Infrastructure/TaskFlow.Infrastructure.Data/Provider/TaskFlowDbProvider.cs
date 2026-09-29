using EF.Common;
using EF.Data.Contracts;
using EF.Data.PostgreSql;
using EF.Data.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using TaskFlow.Hosting;

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
    PgBouncerMode PoolerMode = PgBouncerMode.None)
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

/// <summary>
/// Resolves the PgBouncer pooling mode the PostgreSQL connection string must cooperate with from
/// <c>Database:PostgreSql:PoolerMode</c> (default <see cref="PgBouncerMode.None"/>); no env override (D-045).
/// </summary>
public static class PoolerModeSelector
{
    public const string ConfigurationKey = "Database:PostgreSql:PoolerMode";

    public static PgBouncerMode Resolve(IConfiguration configuration) =>
        configuration.GetEnum(ConfigurationKey, PgBouncerMode.None);
}

/// <summary>Resolves the strict lane's relational provider through the shared D-060 contract.</summary>
public static class TaskFlowDbProviderSelector
{
    public const string EnvironmentVariable = HostingLaneResolver.DatabaseEnvironmentVariable;
    public const string ConfigurationKey = HostingLaneResolver.DatabaseConfigurationKey;
    public const string SqlServerMigrationsAssembly = "TaskFlow.Infrastructure.Data.Migrations.SqlServer";
    public const string PostgreSqlMigrationsAssembly = "TaskFlow.Infrastructure.Data.Migrations.PostgreSql";

    public static TaskFlowDbProvider Resolve(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        return Parse(HostingLaneResolver.Resolve(configuration).Database);
    }

    public static TaskFlowDbProvider ResolveFromEnvironment()
    {
        return Parse(HostingLaneResolver.ResolveFromEnvironment().Database);
    }

    public static string MigrationsAssembly(TaskFlowDbProvider provider) => provider switch
    {
        TaskFlowDbProvider.SqlServer => SqlServerMigrationsAssembly,
        TaskFlowDbProvider.PostgreSql => PostgreSqlMigrationsAssembly,
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, null)
    };

    private static TaskFlowDbProvider Parse(string value) =>
        StrictEnum.Parse<TaskFlowDbProvider>(value, "database provider");
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
        ArgumentNullException.ThrowIfNull(providerOptions);
        var settings = new RelationalProviderSettings
        {
            ConnectionString = providerOptions.ConnectionString,
            MigrationsAssembly = TaskFlowDbProviderSelector.MigrationsAssembly(providerOptions.Provider),
            MigrationsHistoryTable = providerOptions.MigrationsHistoryTable,
            MigrationsHistorySchema = providerOptions.MigrationsHistorySchema,
            MaxRetryCount = providerOptions.MaxRetryCount,
            MaxRetryDelay = TimeSpan.FromSeconds(providerOptions.MaxRetryDelaySeconds),
            CommandTimeoutSeconds = providerOptions.CommandTimeoutSeconds
        };

        return providerOptions.Provider switch
        {
            // The Azure SQL flavor (Azure-tuned execution strategy) is chosen by the data-source host suffix,
            // including the sovereign clouds; container and on-prem servers get the generic SQL Server provider.
            TaskFlowDbProvider.SqlServer => options.UseSqlServerProvider(settings, providerOptions.CompatibilityLevel),
            // D-040: registers the pgvector type handler. Unconditional on this arm because the model branch in
            // OnModelCreating is unconditional on Npgsql too; without it a context that maps TaskItemEmbedding
            // cannot read or write the column at all. D-045: transaction pooling sets the PgBouncer flags.
            TaskFlowDbProvider.PostgreSql => options.UsePostgreSqlProvider(
                settings, providerOptions.PoolerMode, npgsql => npgsql.UseVector()),
            _ => throw new ArgumentOutOfRangeException(nameof(providerOptions), providerOptions.Provider, null)
        };
    }
}
