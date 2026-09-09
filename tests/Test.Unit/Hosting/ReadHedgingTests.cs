using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;

namespace Test.Unit.Hosting;

/// <summary>
/// D-051: hedging must accelerate slow reads and must never duplicate a write. The primary handler here is
/// slower than the hedging delay, so the latency trigger fires whenever it is allowed to - which makes the
/// POST case the real assertion: zero hedged attempts, because a duplicated non-idempotent write is a
/// duplicated side effect.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class ReadHedgingTests
{
    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>A GET slower than the delay is hedged exactly once and served by the faster attempt.</summary>
    [TestMethod]
    public async Task Get_SlowerThanTheHedgingDelay_IssuesOneHedgedAttempt()
    {
        var handler = new CountingHandler(firstAttemptDelay: TimeSpan.FromSeconds(5));
        using var client = BuildClient(handler, enabled: true);

        using var response = await client.GetAsync(new Uri("http://read/tasks"), TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, handler.Attempts, "one original attempt plus one hedged attempt");
    }

    /// <summary>A POST just as slow is never hedged: no second write leaves the process.</summary>
    [TestMethod]
    public async Task Post_SlowerThanTheHedgingDelay_IsNeverHedged()
    {
        var handler = new CountingHandler(firstAttemptDelay: TimeSpan.FromMilliseconds(400));
        using var client = BuildClient(handler, enabled: true);

        using var response = await client.PostAsync(
            new Uri("http://read/tasks"), content: null, TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(1, handler.Attempts, "a write must be sent exactly once");
    }

    /// <summary>Disabled leaves the pipeline alone: a slow GET is simply awaited.</summary>
    [TestMethod]
    public async Task Get_WhenHedgingDisabled_IssuesNoHedgedAttempt()
    {
        var handler = new CountingHandler(firstAttemptDelay: TimeSpan.FromMilliseconds(400));
        using var client = BuildClient(handler, enabled: false);

        using var response = await client.GetAsync(new Uri("http://read/tasks"), TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(1, handler.Attempts);
    }

    private static HttpClient BuildClient(HttpMessageHandler handler, bool enabled)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Resilience:Hedging:Enabled"] = enabled ? "true" : "false",
                ["Resilience:Hedging:DelayMs"] = "50",
                ["Resilience:Hedging:MaxHedgedAttempts"] = "1"
            })
            .Build();

        var services = new ServiceCollection();
        services.AddHttpClient("read")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddReadHedging(config);

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IHttpClientFactory>().CreateClient("read");
    }

    /// <summary>Counts attempts and stalls only the first, so a hedged attempt is the one that answers.</summary>
    private sealed class CountingHandler(TimeSpan firstAttemptDelay) : HttpMessageHandler
    {
        private int _attempts;

        internal int Attempts => Volatile.Read(ref _attempts);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                await Task.Delay(firstAttemptDelay, cancellationToken);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        }
    }
}
