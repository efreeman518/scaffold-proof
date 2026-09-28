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

    /// <summary>
    /// Every hedged attempt sends its own request message. Two attempts sharing one instance would race on
    /// the headers each send mutates; only the standard hedging pipeline snapshots and clones the request.
    /// </summary>
    [TestMethod]
    public async Task HedgedGet_AttemptsUseDistinctRequestMessages()
    {
        var handler = new CountingHandler(firstAttemptDelay: TimeSpan.FromSeconds(5));
        using var client = BuildClient(handler, enabled: true);

        using var response = await client.GetAsync(new Uri("http://read/tasks"), TestContext.CancellationToken);

        var requests = handler.Requests;
        Assert.HasCount(2, requests);
        Assert.IsFalse(ReferenceEquals(requests[0], requests[1]), "each hedged attempt needs its own request");
    }

    /// <summary>HEAD is a read like GET, so a slow one is hedged too.</summary>
    [TestMethod]
    public async Task SlowHead_IsHedged()
    {
        var handler = new CountingHandler(firstAttemptDelay: TimeSpan.FromSeconds(5));
        using var client = BuildClient(handler, enabled: true);

        using var request = new HttpRequestMessage(HttpMethod.Head, new Uri("http://read/tasks"));
        using var response = await client.SendAsync(request, TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(2, handler.Attempts);
    }

    /// <summary>
    /// The outcome trigger is guarded as well as the latency trigger: a fast transient 503 on a POST is
    /// returned as-is, never hedged into a second write.
    /// </summary>
    [TestMethod]
    public async Task TransientPost503_NotHedged()
    {
        var handler = new CountingHandler(TimeSpan.Zero, HttpStatusCode.ServiceUnavailable);
        using var client = BuildClient(handler, enabled: true);

        using var response = await client.PostAsync(
            new Uri("http://read/tasks"), content: null, TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.AreEqual(1, handler.Attempts, "a write must be sent exactly once");
    }

    /// <summary>A negative delay or an attempt count outside Polly's 1..10 fails registration.</summary>
    [TestMethod]
    [DataRow("-1", "1")]
    [DataRow("50", "0")]
    [DataRow("50", "11")]
    public void InvalidSettings_Throw(string delayMs, string maxHedgedAttempts)
    {
        var config = Config(enabled: true, delayMs, maxHedgedAttempts);

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new ServiceCollection().AddHttpClient("read").AddReadHedging(config));
    }

    private static HttpClient BuildClient(HttpMessageHandler handler, bool enabled)
    {
        var config = Config(enabled, delayMs: "50", maxHedgedAttempts: "1");

        var services = new ServiceCollection();
        services.AddHttpClient("read")
            .ConfigurePrimaryHttpMessageHandler(() => handler)
            .AddReadHedging(config);

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IHttpClientFactory>().CreateClient("read");
    }

    private static IConfiguration Config(bool enabled, string delayMs, string maxHedgedAttempts) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Resilience:Hedging:Enabled"] = enabled ? "true" : "false",
                ["Resilience:Hedging:DelayMs"] = delayMs,
                ["Resilience:Hedging:MaxHedgedAttempts"] = maxHedgedAttempts
            })
            .Build();

    /// <summary>
    /// Counts attempts, records each request instance, and stalls only the first, so a hedged attempt is the
    /// one that answers.
    /// </summary>
    private sealed class CountingHandler(TimeSpan firstAttemptDelay, HttpStatusCode status = HttpStatusCode.OK)
        : HttpMessageHandler
    {
        private readonly List<HttpRequestMessage> _requests = [];
        private int _attempts;

        internal int Attempts => Volatile.Read(ref _attempts);

        internal HttpRequestMessage[] Requests
        {
            get
            {
                lock (_requests) return [.. _requests];
            }
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            lock (_requests) _requests.Add(request);
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                await Task.Delay(firstAttemptDelay, cancellationToken);
            }

            return new HttpResponseMessage(status);
        }
    }
}
