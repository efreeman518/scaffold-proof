using EF.Data.Encryption;
using EF.IntegrationTesting.Testcontainers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.ComponentModel;
using TaskFlow.Hosting;
using TaskFlow.Infrastructure.Data.Interceptors;
using TaskFlow.Infrastructure.Data.Provider;

namespace Test.Support.Hosting;

/// <summary>Strict D-060 lane selection for container-backed tests.</summary>
public static class TestHostingLane
{
    public const string LegacyDatabaseProviderEnvironmentVariable = "TASKFLOW_TEST_DB_PROVIDER";

    public static HostingLaneSettings Current
    {
        get
        {
            var configuredLane = Environment.GetEnvironmentVariable(HostingLaneResolver.LaneEnvironmentVariable);
            var legacyDatabase = Environment.GetEnvironmentVariable(LegacyDatabaseProviderEnvironmentVariable);
            var legacyProvider = string.IsNullOrWhiteSpace(legacyDatabase)
                ? (TaskFlowDbProvider?)null
                : legacyDatabase.Trim() switch
                {
                    var value when value.Equals("SqlServer", StringComparison.OrdinalIgnoreCase) =>
                        TaskFlowDbProvider.SqlServer,
                    var value when value.Equals("PostgreSql", StringComparison.OrdinalIgnoreCase) =>
                        TaskFlowDbProvider.PostgreSql,
                    _ => throw new ArgumentException(
                        $"Unknown legacy test database provider '{legacyDatabase}'. Allowed values: SqlServer, PostgreSql.")
                };

            var lane = string.IsNullOrWhiteSpace(configuredLane) && legacyProvider is not null
                ? legacyProvider == TaskFlowDbProvider.SqlServer ? "Azure" : "NonAzure"
                : configuredLane;
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [HostingLaneResolver.LaneConfigurationKey] = lane
                })
                .Build();
            var settings = HostingLaneResolver.Resolve(configuration);

            if (legacyProvider is not null && legacyProvider != DatabaseProviderFor(settings))
            {
                throw new InvalidOperationException(
                    $"{LegacyDatabaseProviderEnvironmentVariable}={legacyProvider} conflicts with " +
                    $"{HostingLaneResolver.LaneEnvironmentVariable}={settings.Lane}. Remove the deprecated variable.");
            }

            return settings;
        }
    }

    public static TaskFlowDbProvider DatabaseProvider => DatabaseProviderFor(Current);

    public static bool UsesMongoDb => Current.ReadModel.Equals("MongoDb", StringComparison.Ordinal);

    private static TaskFlowDbProvider DatabaseProviderFor(HostingLaneSettings settings) =>
        settings.Lane == HostingLane.Azure ? TaskFlowDbProvider.SqlServer : TaskFlowDbProvider.PostgreSql;
}

/// <summary>Deprecated database-only selector retained for external test extensions.</summary>
[Obsolete("Use TASKFLOW_LANE=Azure|NonAzure through TestHostingLane.")]
[EditorBrowsable(EditorBrowsableState.Never)]
public static class TestDbProvider
{
    public const string EnvironmentVariable = TestHostingLane.LegacyDatabaseProviderEnvironmentVariable;

    public static TaskFlowDbProvider Current => TestHostingLane.DatabaseProvider;
}

/// <summary>
/// One database container for the selected provider, plus the two things every container-backed test needs:
/// an isolated empty database and context options that match the runtime wiring (UseTaskFlowProvider, test
/// column encryption, Version and blind-index interceptors).
/// </summary>
public sealed class TestDatabaseContainer(TaskFlowDbProvider provider) : IAsyncDisposable
{
    private readonly MsSqlContainerFixture? _sql = provider == TaskFlowDbProvider.SqlServer
        ? new(ContainerImages.SqlServer)
        : null;
    private readonly PostgreSqlContainerFixture? _postgres = provider == TaskFlowDbProvider.PostgreSql
        ? new(ContainerImages.PostgreSql)
        : null;

    public TaskFlowDbProvider Provider { get; } = provider;

    public string ConnectionString => _sql?.ConnectionString ?? _postgres!.ConnectionString;

    public Task StartAsync() => _sql?.StartAsync() ?? _postgres!.StartAsync();

    public ValueTask DisposeAsync() => _sql?.DisposeAsync() ?? _postgres!.DisposeAsync();

    /// <summary>Creates an empty database on the running container and returns a connection string pointing at it.</summary>
    public async Task<string> CreateEmptyDatabaseAsync(string prefix)
    {
        var databaseName = $"{prefix}_{Guid.NewGuid():N}";
        if (Provider == TaskFlowDbProvider.SqlServer)
        {
            var target = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = databaseName };
            var admin = new SqlConnectionStringBuilder(ConnectionString) { InitialCatalog = "master" };
            await using var connection = new SqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE [{databaseName}]";
            await command.ExecuteNonQueryAsync();
            return target.ConnectionString;
        }
        else
        {
            var target = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = databaseName };
            var admin = new NpgsqlConnectionStringBuilder(ConnectionString) { Database = "postgres" };
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
            await command.ExecuteNonQueryAsync();
            return target.ConnectionString;
        }
    }

    /// <summary>
    /// Context options mirroring the Bootstrapper wiring for the selected provider.
    /// <paramref name="extraInterceptors"/> is for tests that observe the context itself (a command
    /// counter, for instance): interceptors can only be supplied when the options are built, never on
    /// a live context.
    /// </summary>
    public DbContextOptions<TContext> BuildOptions<TContext>(
        string? connectionString,
        string migrationsHistoryTable,
        string migrationsHistorySchema,
        params IInterceptor[] extraInterceptors)
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseTaskFlowProvider(new TaskFlowProviderOptions(
                Provider,
                connectionString ?? ConnectionString,
                migrationsHistoryTable,
                migrationsHistorySchema))
            .UseColumnEncryption(TestColumnEncryption.Encryptor)
            .AddInterceptors(
                new VersionTimestampInterceptor(),
                // D-026: the same interceptor the hosts register, so integration tests exercise the real
                // staging path rather than a hand-inserted outbox row.
                new OutboxStagingInterceptor(),
                new BlindIndexInterceptor(TestColumnEncryption.Keys.BlindIndexKey))
            .AddInterceptors(extraInterceptors)
            .Options;
}
