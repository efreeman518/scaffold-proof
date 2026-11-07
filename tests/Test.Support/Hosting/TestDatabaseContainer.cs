using EF.Data.Encryption;
using EF.IntegrationTesting.PostgreSql;
using EF.IntegrationTesting.SqlServer;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Npgsql;
using TaskFlow.Hosting;
using TaskFlow.Infrastructure.Data.Provider;

namespace Test.Support.Hosting;

/// <summary>Strict D-060 lane selection for container-backed tests.</summary>
public static class TestHostingLane
{
    public static HostingLaneSettings Current
    {
        get
        {
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [HostingLaneResolver.LaneConfigurationKey] =
                        Environment.GetEnvironmentVariable(HostingLaneResolver.LaneEnvironmentVariable)
                })
                .Build();
            return HostingLaneResolver.Resolve(configuration);
        }
    }

    public static TaskFlowDbProvider DatabaseProvider => DatabaseProviderFor(Current);

    public static bool UsesMongoDb => Current.ReadModel.Equals("MongoDb", StringComparison.Ordinal);

    private static TaskFlowDbProvider DatabaseProviderFor(HostingLaneSettings settings) =>
        settings.Lane == HostingLane.Azure ? TaskFlowDbProvider.SqlServer : TaskFlowDbProvider.PostgreSql;
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

    /// <summary>Whether the container started and has not been disposed.</summary>
    public bool IsStarted => _sql is not null ? _sql.IsStarted : _postgres!.IsStarted;

    /// <summary>Why the start attempt failed, or <see langword="null"/>; recorded by the package fixture, never thrown.</summary>
    public Exception? StartupError => _sql is not null ? _sql.StartupError : _postgres!.StartupError;

    /// <summary>Starts the container once; a failure is recorded in <see cref="StartupError"/> and not retried.</summary>
    public Task StartAsync(CancellationToken cancellationToken = default) =>
        _sql?.StartAsync(cancellationToken) ?? _postgres!.StartAsync(cancellationToken);

    public ValueTask DisposeAsync() => _sql?.DisposeAsync() ?? _postgres!.DisposeAsync();

    /// <summary>
    /// Creates an empty database named <c>{prefix}_{guid}</c> on the running container and returns a connection
    /// string pointing at it. The package fixture validates the prefix (1-30 characters, a letter then letters,
    /// digits or underscores) so the name fits PostgreSQL's 63-character identifier limit and is safe in DDL.
    /// On PostgreSQL the string is unpooled (<see cref="UnpooledPostgreSql"/>).
    /// </summary>
    public async Task<string> CreateEmptyDatabaseAsync(string prefix, CancellationToken cancellationToken = default) =>
        _sql is not null
            ? await _sql.CreateDatabaseAsync(prefix, cancellationToken).ConfigureAwait(false)
            : UnpooledPostgreSql(await _postgres!.CreateDatabaseAsync(prefix, cancellationToken).ConfigureAwait(false));

    /// <summary>
    /// A throwaway database is never used again after its test, so a pooled connection to it keeps one of the
    /// container's <c>max_connections</c> (100) slots idle until Npgsql prunes it (Connection Idle Lifetime, 300 s).
    /// Across a serial run those idle connections add up, and a later test fails with 53300 "too many clients".
    /// Unpooled, a connection holds a slot only while it is open; every context, factory and in-process host built
    /// on the string inherits it. SQL Server's connection limit (32767) is not reachable this way, so its string
    /// keeps the default pool.
    /// </summary>
    public static string UnpooledPostgreSql(string connectionString) =>
        new NpgsqlConnectionStringBuilder(connectionString) { Pooling = false }.ConnectionString;

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
                // D-026: the same interceptor the hosts register, so integration tests exercise the real
                // staging path rather than a hand-inserted outbox row.
                TestOutbox.Interceptor(),
                new BlindIndexInterceptor(TestColumnEncryption.Keys.BlindIndexKey))
            .AddInterceptors(extraInterceptors)
            .Options;
}
