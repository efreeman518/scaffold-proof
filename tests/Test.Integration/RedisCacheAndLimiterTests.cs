using EF.Cache;
using EF.RateLimiting;
using EF.RateLimiting.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Infrastructure.Caching;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// Validates the two behaviors that only exist across processes and therefore cannot be proven by any
/// in-process test: a tag invalidation that reaches another replica's L1 through the backplane, and a rate
/// limit that is one budget shared by every replica rather than one budget each. The fail-open policy with Redis
/// unreachable needs no container and is pinned in process by Test.Unit's TenantRateLimitingCompositionTests.
/// The limiter is EF.RateLimiting.Redis over the cache's shared multiplexer, composed the way the Api composes it.
/// Component tier: a real Redis Testcontainer; two service providers stand in for two replicas.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class RedisCacheAndLimiterTests
{
    /// <summary>Inconclusive without a container runtime; fails when the Redis container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("Redis", RedisContainerFixture.StartupError);

    /// <summary>
    /// Two cache instances backed by one Redis: a value cached on the first is served from the second's L2,
    /// and a tag invalidation on the first evicts it from the second's L1 through the backplane.
    /// </summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task RemoveByTagAsync_EvictsTheOtherReplicasL1()
    {
        var tenantId = Guid.NewGuid();
        var key = TaskFlowCache.Key(CacheKind.TaskMetadata, tenantId);
        var tags = TaskFlowCache.TagsFor(CacheKind.TaskMetadata, tenantId);

        var replicaA = BuildCache();
        var replicaB = BuildCache();

        var factoryCalls = 0;
        Task<string> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref factoryCalls);
            return Task.FromResult($"value-{factoryCalls}");
        }

        var first = await replicaA.GetOrSetAsync(key, Factory, CacheProfiles.Metadata, tags, TestContext.CancellationToken);
        // The second replica has an empty L1 but reads the shared L2, so the factory does not run again.
        var second = await replicaB.GetOrSetAsync(key, Factory, CacheProfiles.Metadata, tags, TestContext.CancellationToken);

        Assert.AreEqual(first, second);
        Assert.AreEqual(1, factoryCalls, "the distributed cache served the second replica");

        await replicaA.RemoveByTagAsync(
            CacheTags.Entity(tenantId, CacheTags.Category), TestContext.CancellationToken);

        // Backplane notifications are asynchronous; poll rather than sleeping a fixed interval.
        var evicted = await WaitUntilAsync(async () =>
        {
            var value = await replicaB.GetOrSetAsync(key, Factory, CacheProfiles.Metadata, tags, TestContext.CancellationToken);
            return value != first;
        });

        Assert.IsTrue(evicted, "the tag invalidation reached the other replica's L1");
        Assert.IsGreaterThan(1, factoryCalls);
    }

    /// <summary>Two limiter instances share one budget: the permits are consumed once, not once each.</summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task TwoLimiterInstances_ShareOneRedisBudget()
    {
        var key = $"rl:IntegrationTest:default:tenant:{{{Guid.NewGuid()}}}";
        var allowance = new RateLimitAllowance { PermitLimit = 4, WindowSeconds = 60 };

        using var replicaA = BuildLimiterReplica(RedisContainerFixture.ConnectionString);
        using var replicaB = BuildLimiterReplica(RedisContainerFixture.ConnectionString);

        using var limiterA = replicaA.GetRequiredService<ISlidingWindowLimiterFactory>().Create(key, allowance);
        using var limiterB = replicaB.GetRequiredService<ISlidingWindowLimiterFactory>().Create(key, allowance);

        var acquired = 0;
        for (var i = 0; i < 3; i++)
        {
            if ((await limiterA.AcquireAsync(1, TestContext.CancellationToken)).IsAcquired) acquired++;
            if ((await limiterB.AcquireAsync(1, TestContext.CancellationToken)).IsAcquired) acquired++;
        }

        Assert.AreEqual(4, acquired,
            "six attempts against a shared budget of four: two replicas do not get four permits each");
    }

    /// <summary>Builds one cache "replica" over the shared Redis.</summary>
    private static ITypedCache BuildCache()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:0:Name"] = AppConstants.DEFAULT_CACHE,
                ["CacheSettings:0:RedisConnectionStringName"] = "Redis1",
                ["ConnectionStrings:Redis1"] = RedisContainerFixture.ConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddTaskFlowCaching(config);

        return services.BuildServiceProvider().GetRequiredService<ITypedCache>();
    }

    /// <summary>
    /// Builds one limiter "replica": the cache (which owns the shared multiplexer), then the tenant limiter with the
    /// Redis backend over that multiplexer - the Api's registration order.
    /// </summary>
    private static ServiceProvider BuildLimiterReplica(string connectionString)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:0:Name"] = AppConstants.DEFAULT_CACHE,
                ["CacheSettings:0:RedisConnectionStringName"] = "Redis1",
                ["ConnectionStrings:Redis1"] = connectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new TestHostEnvironment());
        services.AddTaskFlowCaching(config);
        services.AddTenantRateLimiting(config);
        Assert.IsTrue(services.HasSharedRedis(), "the cache registered the shared multiplexer");
        services.AddRedisRateLimiting();
        return services.BuildServiceProvider();
    }

    /// <summary>Polls <paramref name="condition"/> until it holds or the budget runs out.</summary>
    private static async Task<bool> WaitUntilAsync(Func<Task<bool>> condition)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (await condition()) return true;
            await Task.Delay(100);
        }

        return false;
    }

    /// <summary>Minimal host environment: the cache only reads the environment name for its key prefix.</summary>
    private sealed class TestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "IntegrationTest";
        public string ApplicationName { get; set; } = "Test.Integration";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    public TestContext TestContext { get; set; } = null!;
}
