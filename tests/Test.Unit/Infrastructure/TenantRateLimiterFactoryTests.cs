using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskFlow.Infrastructure.Caching.RateLimiting;
using TaskFlow.Observability.Meters;

namespace Test.Unit.Infrastructure;

/// <summary>
/// The tenant limiter must fail open, not closed, when Redis is unreachable at the moment the first partition
/// is built - including with the default connection string Aspire and compose emit, which leaves
/// <c>abortConnect</c> at its throwing default.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class TenantRateLimiterFactoryTests
{
    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// A dead endpoint without abortConnect=false: building the partition limiter must not throw (that would
    /// 500 every request needing a new partition until restart), and acquiring must admit.
    /// </summary>
    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task CreateTenantLimiter_RedisUnreachableWithDefaultConnectionString_FailsOpen()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Redis1"] = "127.0.0.1:1,connectTimeout=250,connectRetry=1"
            })
            .Build();
        var factory = new TenantRateLimiterFactory(
            Options.Create(new RateLimitingSettings()),
            config,
            new RateLimitingMeter(),
            NullLogger<TenantRateLimiterFactory>.Instance);

        using var limiter = factory.CreateTenantLimiter(Guid.NewGuid().ToString());
        using var lease = await limiter.AcquireAsync(1, TestContext.CancellationToken);

        Assert.IsTrue(factory.IsDistributed);
        Assert.IsTrue(lease.IsAcquired, "an unreachable limiter backend admits the request");
    }
}
