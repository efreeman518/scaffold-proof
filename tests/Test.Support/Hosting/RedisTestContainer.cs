using Testcontainers.Redis;

namespace Test.Support.Hosting;

/// <summary>
/// Redis Testcontainer for the cache-backplane and distributed-limiter lanes. Both behaviors only exist
/// across processes - a shared rate-limit budget and a tag invalidation that reaches another replica's L1 -
/// so neither can be proven against an in-process fake.
/// </summary>
public sealed class RedisTestContainer : IAsyncDisposable
{
    /// <summary>Pinned image so a lane failure is a code change, not an upstream tag moving.</summary>
    public const string DefaultImage = "redis:7.4-alpine";

    private readonly RedisContainer _container = new RedisBuilder(DefaultImage).Build();

    /// <summary>True once the container has started.</summary>
    public bool IsStarted { get; private set; }

    /// <summary>StackExchange.Redis connection string for the running container.</summary>
    public string ConnectionString => _container.GetConnectionString();

    /// <summary>Starts the container.</summary>
    public async Task StartAsync()
    {
        await _container.StartAsync();
        IsStarted = true;
    }

    /// <summary>Disposes the container.</summary>
    public async ValueTask DisposeAsync()
    {
        if (!IsStarted) return;
        await _container.DisposeAsync();
        IsStarted = false;
    }
}
