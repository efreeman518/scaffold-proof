using EF.RateLimiting;
using EF.RateLimiting.Redis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using TaskFlow.Application.Contracts;
using TaskFlow.Infrastructure.Caching;

namespace Test.Unit.Infrastructure;

/// <summary>
/// The Api's limiter composition (S10/S11): the Redis backend is added exactly when the cache registered its shared
/// multiplexer, and it fails open, not closed, when Redis is unreachable - including with the default connection
/// string Aspire and compose emit, which leaves <c>abortConnect</c> at its throwing default (EF.Cache forces it false).
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class TenantRateLimitingCompositionTests
{
    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A dead endpoint: building the partition limiter must not throw (that would 500 every request needing a new
    /// partition until restart), and acquiring past the budget must admit.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task RedisConfiguredButUnreachable_LimiterFailsOpen()
    {
        // asyncTimeout bounds each command queued behind the dead connection; at the 5 s default the two
        // acquisitions cost about 12 s. The limiter's fail-open path is the same at either timeout.
        using var provider = Build("127.0.0.1:1,connectTimeout=250,connectRetry=1,asyncTimeout=250,syncTimeout=250");

        var factory = provider.GetRequiredService<ISlidingWindowLimiterFactory>();
        using var limiter = factory.Create($"rl:test:default:tenant:{{{Guid.NewGuid()}}}", new RateLimitAllowance { PermitLimit = 1 });
        using var first = await limiter.AcquireAsync(1, TestContext.CancellationToken);
        using var second = await limiter.AcquireAsync(1, TestContext.CancellationToken);

        Assert.AreNotEqual("InProcessSlidingWindowLimiterFactory", factory.GetType().Name, "Redis is configured");
        Assert.IsTrue(first.IsAcquired && second.IsAcquired, "an unreachable limiter backend admits the request");
    }

    /// <summary>Without Redis the budgets stay in process: correct on one replica, and no multiplexer is needed.</summary>
    [TestMethod]
    public void NoRedis_UsesTheInProcessLimiter()
    {
        using var provider = Build(redisConnectionString: null);

        Assert.AreEqual(
            "InProcessSlidingWindowLimiterFactory",
            provider.GetRequiredService<ISlidingWindowLimiterFactory>().GetType().Name);
    }

    /// <summary>The Api's registration order: cache first, then the tenant limiter, Redis only with the shared connection.</summary>
    private static ServiceProvider Build(string? redisConnectionString)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["CacheSettings:0:Name"] = AppConstants.DEFAULT_CACHE,
            ["CacheSettings:0:RedisConnectionStringName"] = "Redis1",
            ["ConnectionStrings:Redis1"] = redisConnectionString,
            ["OpenTelemetry:MetricsEnabled"] = "false"
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = "Testing" });
        services.AddTaskFlowCaching(config);
        services.AddTenantRateLimiting(config);
        if (services.HasSharedRedis())
            services.AddRedisRateLimiting();
        return services.BuildServiceProvider();
    }
}
