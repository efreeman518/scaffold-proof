using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using System.Globalization;
using System.Net;
using System.Threading.RateLimiting;
using TaskFlow.Gateway;

namespace Test.Unit.Gateway;

/// <summary>
/// Exercises the D-050 edge limiter through the limiter the gateway actually registers, not a copy of its
/// options: a burst past the per-IP bucket is shed with 429 plus Retry-After, a second client is unaffected,
/// probe routes are exempt, and turning the feature off leaves no global limiter at all.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class GatewayEdgeRateLimitTests
{
    private const int Tokens = 5;

    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>A burst past the bucket is rejected, and the rejection is a 429 carrying Retry-After.</summary>
    [TestMethod]
    public async Task GlobalLimiter_BurstOverTheBucket_ShedsWith429AndRetryAfter()
    {
        var options = BuildLimiterOptions(enabled: true);
        var leases = new List<RateLimitLease>();

        try
        {
            for (var i = 0; i < Tokens; i++)
            {
                var lease = await options.GlobalLimiter!.AcquireAsync(Request("203.0.113.10"), 1, TestContext.CancellationToken);
                leases.Add(lease);
                Assert.IsTrue(lease.IsAcquired, $"request {i + 1} of the bucket should be admitted");
            }

            using var rejected = await options.GlobalLimiter!.AcquireAsync(
                Request("203.0.113.10"), 1, TestContext.CancellationToken);
            Assert.IsFalse(rejected.IsAcquired, "the request past the bucket must be shed, not queued");
            Assert.AreEqual(StatusCodes.Status429TooManyRequests, options.RejectionStatusCode);

            var context = Request("203.0.113.10");
            await options.OnRejected!(new OnRejectedContext { HttpContext = context, Lease = rejected }, TestContext.CancellationToken);
            Assert.IsFalse(
                StringValues.IsNullOrEmpty(context.Response.Headers.RetryAfter),
                "a shed client needs the backoff, not a bare 429");

            // Partitioned by client IP: exhausting one caller must not shed the next one.
            using var otherClient = await options.GlobalLimiter!.AcquireAsync(
                Request("203.0.113.11"), 1, TestContext.CancellationToken);
            Assert.IsTrue(otherClient.IsAcquired);
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }

    /// <summary>Probe routes are exempt: shedding a probe is how a healthy replica gets restarted.</summary>
    [TestMethod]
    public async Task GlobalLimiter_ProbeRoutes_AreNeverShed()
    {
        var options = BuildLimiterOptions(enabled: true);
        var leases = new List<RateLimitLease>();

        try
        {
            for (var i = 0; i < Tokens + 3; i++)
            {
                var lease = await options.GlobalLimiter!.AcquireAsync(
                    Request("203.0.113.12", "/healthz/ready"), 1, TestContext.CancellationToken);
                leases.Add(lease);
                Assert.IsTrue(lease.IsAcquired);
            }
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
    }

    /// <summary>Disabled means absent, not a limiter with an infinite budget.</summary>
    [TestMethod]
    public void GlobalLimiter_WhenEdgeDisabled_IsNotRegistered()
    {
        var options = BuildLimiterOptions(enabled: false);

        Assert.IsNull(options.GlobalLimiter);
    }

    private static RateLimiterOptions BuildLimiterOptions(bool enabled)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["CorsSettings:AllowedOrigins:0"] = "https://localhost";
        builder.Configuration["RateLimiting:Edge:Enabled"] = enabled ? "true" : "false";
        builder.Configuration["RateLimiting:Edge:TokensPerPeriod"] = Tokens.ToString(CultureInfo.InvariantCulture);
        builder.Configuration["RateLimiting:Edge:MaxConcurrentRequests"] = "1000";

        builder.AddServiceDefaults();
        builder.Services.AddGatewayServices(builder.Configuration);

        using var provider = builder.Services.BuildServiceProvider();
        return provider.GetRequiredService<IOptions<RateLimiterOptions>>().Value;
    }

    private static HttpContext Request(string clientIp, string path = "/api/taskitems") => new DefaultHttpContext
    {
        Connection = { RemoteIpAddress = IPAddress.Parse(clientIp) },
        Request = { Path = path }
    };
}
