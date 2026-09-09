using Test.Support.Hosting;

namespace Test.Integration.Infrastructure;

/// <summary>
/// Redis Testcontainer for the cache-backplane and distributed-limiter lanes. Started with the other
/// component-tier containers; <see cref="StartupError"/> is captured so dependent tests fail with its
/// diagnostics instead of aborting assembly discovery.
/// </summary>
internal static class RedisContainerFixture
{
    private static readonly RedisTestContainer Container = new();

    /// <summary>Startup failure captured by <see cref="StartAsync"/>; null when the container started cleanly.</summary>
    internal static Exception? StartupError { get; private set; }

    /// <summary>StackExchange.Redis connection string. Only valid once startup succeeded.</summary>
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
}
