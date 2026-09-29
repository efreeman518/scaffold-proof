using EF.IntegrationTesting.Testcontainers;
using TaskFlow.Hosting;
using Testcontainers.Redis;

namespace Test.Integration.Infrastructure;

/// <summary>
/// Redis Testcontainer for the cache-backplane and distributed-limiter lanes. Started with the other
/// component-tier containers; <see cref="StartupError"/> is recorded by the fixture so dependent tests fail with
/// its diagnostics instead of aborting assembly discovery.
/// </summary>
internal static class RedisContainerFixture
{
    private static readonly ContainerFixture<RedisContainer> Redis = new(() => new RedisBuilder(ContainerImages.Redis).Build());

    /// <summary>Startup failure recorded by the fixture; null when the container started cleanly.</summary>
    internal static Exception? StartupError => Redis.StartupError;

    /// <summary>StackExchange.Redis connection string. Only valid once startup succeeded.</summary>
    internal static string ConnectionString => Redis.Container.GetConnectionString();

    /// <summary>Starts the container; a post-preflight failure is kept in <see cref="StartupError"/>.</summary>
    internal static Task StartAsync(CancellationToken cancellationToken = default) => Redis.StartAsync(cancellationToken);

    /// <summary>Disposes the container.</summary>
    internal static Task StopAsync() => Redis.DisposeAsync().AsTask();
}
