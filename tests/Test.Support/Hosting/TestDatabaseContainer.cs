using EF.IntegrationTesting.Testcontainers;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TaskFlow.Infrastructure.Data.Encryption;
using TaskFlow.Infrastructure.Data.Interceptors;
using TaskFlow.Infrastructure.Data.Provider;
using Testcontainers.PostgreSql;

namespace Test.Support.Hosting;

/// <summary>Lane selection for container-backed tests: env <c>TASKFLOW_TEST_DB_PROVIDER</c>, default SqlServer.</summary>
public static class TestDbProvider
{
    public const string EnvironmentVariable = "TASKFLOW_TEST_DB_PROVIDER";

    public static TaskFlowDbProvider Current =>
        Environment.GetEnvironmentVariable(EnvironmentVariable) is { Length: > 0 } value
            ? Enum.Parse<TaskFlowDbProvider>(value, ignoreCase: true)
            : TaskFlowDbProvider.SqlServer;
}

// fallback: replace with EF.IntegrationTesting.PostgreSqlContainerFixture when published (package request 8).
/// <summary>PostgreSQL 17 + pgvector Testcontainer with the same surface as the package MsSqlContainerFixture.</summary>
public sealed class PostgreSqlContainerFixture(string image = PostgreSqlContainerFixture.DefaultImage) : IAsyncDisposable
{
    public const string DefaultImage = "pgvector/pgvector:pg17";

    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder(image).Build();

    public bool IsStarted { get; private set; }

    public string ConnectionString => _container.GetConnectionString();

    public async Task StartAsync()
    {
        await _container.StartAsync();
        IsStarted = true;
    }

    public async ValueTask DisposeAsync()
    {
        if (!IsStarted) return;
        await _container.DisposeAsync();
        IsStarted = false;
    }
}

/// <summary>
/// One database container for the selected provider, plus the two things every container-backed test needs:
/// an isolated empty database and context options that match the runtime wiring (UseTaskFlowProvider, test
/// column encryption, Version and blind-index interceptors).
/// </summary>
public sealed class TestDatabaseContainer(TaskFlowDbProvider provider) : IAsyncDisposable
{
    private readonly MsSqlContainerFixture? _sql = provider == TaskFlowDbProvider.SqlServer ? new() : null;
    private readonly PostgreSqlContainerFixture? _postgres = provider == TaskFlowDbProvider.PostgreSql ? new() : null;

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

    /// <summary>Context options mirroring the Bootstrapper wiring for the selected provider.</summary>
    public DbContextOptions<TContext> BuildOptions<TContext>(
        string? connectionString,
        string migrationsHistoryTable,
        string migrationsHistorySchema)
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
                new BlindIndexInterceptor(TestColumnEncryption.Keys.BlindIndexKey))
            .Options;
}
