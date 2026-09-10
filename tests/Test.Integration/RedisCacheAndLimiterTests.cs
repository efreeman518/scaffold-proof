using EF.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Infrastructure.Caching;
using TaskFlow.Infrastructure.Caching.RateLimiting;
using TaskFlow.Observability.Meters;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// Validates the two behaviors that only exist across processes and therefore cannot be proven by any
/// in-process test: a tag invalidation that reaches another replica's L1 through the backplane, and a rate
/// limit that is one budget shared by every replica rather than one budget each. The third test pins the
/// failure policy - with Redis unreachable the limiter admits the request rather than rejecting it, which is
/// deliberate and must not regress into failing closed.
/// Component tier: a real Redis Testcontainer; two service providers stand in for two replicas.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class RedisCacheAndLimiterTests
{
    /// <summary>Marks the test Inconclusive when the Redis container failed to start.</summary>
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
        var tenantId = Guid.NewGuid().ToString();
        var settings = Tiers(permitLimit: 4, windowSeconds: 60);

        var replicaA = BuildLimiterFactory(settings, RedisContainerFixture.ConnectionString);
        var replicaB = BuildLimiterFactory(settings, RedisContainerFixture.ConnectionString);

        Assert.IsTrue(replicaA.IsDistributed);

        using var limiterA = replicaA.CreateTenantLimiter(tenantId);
        using var limiterB = replicaB.CreateTenantLimiter(tenantId);

        var acquired = 0;
        for (var i = 0; i < 3; i++)
        {
            if ((await limiterA.AcquireAsync(1, TestContext.CancellationToken)).IsAcquired) acquired++;
            if ((await limiterB.AcquireAsync(1, TestContext.CancellationToken)).IsAcquired) acquired++;
        }

        Assert.AreEqual(4, acquired,
            "six attempts against a shared budget of four: two replicas do not get four permits each");
    }

    /// <summary>With Redis unreachable the limiter admits the request and counts the backend failure.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task RedisUnreachable_LimiterFailsOpen()
    {
        // A port nothing is listening on, with a short connect timeout so the test is not the retry policy.
        var deadRedis = "127.0.0.1:6399,connectTimeout=250,abortConnect=false,connectRetry=1";
        var factory = BuildLimiterFactory(Tiers(permitLimit: 1, windowSeconds: 60), deadRedis);

        using var limiter = factory.CreateTenantLimiter(Guid.NewGuid().ToString());

        // Two acquisitions against a budget of one: both are admitted because the budget cannot be read.
        Assert.IsTrue((await limiter.AcquireAsync(1, TestContext.CancellationToken)).IsAcquired);
        Assert.IsTrue((await limiter.AcquireAsync(1, TestContext.CancellationToken)).IsAcquired,
            "the limiter fails open rather than rejecting traffic a healthy API could serve");
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

    /// <summary>Builds one limiter "replica" against the given Redis connection string.</summary>
    private static TenantRateLimiterFactory BuildLimiterFactory(RateLimitingSettings settings, string connectionString)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis1"] = connectionString
            })
            .Build();

        return new TenantRateLimiterFactory(
            Options.Create(settings),
            config,
            new RateLimitingMeter(),
            NullLogger<TenantRateLimiterFactory>.Instance);
    }

    /// <summary>Settings with a single tier allowance.</summary>
    private static RateLimitingSettings Tiers(int permitLimit, int windowSeconds) => new()
    {
        DefaultTier = RateLimitingSettings.Standard,
        Tiers = new Dictionary<string, RateLimitTier>(StringComparer.OrdinalIgnoreCase)
        {
            [RateLimitingSettings.Standard] = new() { PermitLimit = permitLimit, WindowSeconds = windowSeconds }
        }
    };

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
