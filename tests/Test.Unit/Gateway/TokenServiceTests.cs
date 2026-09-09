using Azure.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Gateway;

namespace Test.Unit.Gateway;

/// <summary>
/// Validates the gateway's token single-flight. Without it, a burst arriving on an expired token produces one
/// identity-provider call per request - the exact moment the provider is most likely to throttle. The cache
/// holds the in-flight acquisition, so the properties worth pinning are: one acquisition for many concurrent
/// callers, a faulted acquisition is not cached, and a token near expiry is refreshed rather than served.
/// Pure-unit tier: a counting TokenCredential is the SUT's only collaborator.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class TokenServiceTests
{
    private const string ClusterId = "taskflow-api";

    /// <summary>Fifty concurrent callers cause exactly one token acquisition.</summary>
    [TestMethod]
    public async Task GetAccessTokenAsync_FiftyConcurrentCallers_AcquiresOnce()
    {
        var credential = new CountingCredential(_ => DateTimeOffset.UtcNow.AddHours(1));
        var service = Build(credential);

        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => service.GetAccessTokenAsync(ClusterId, TestContext.CancellationToken)));

        Assert.AreEqual(1, credential.Calls);
        Assert.AreEqual(1, results.Distinct().Count(), "every caller received the same token");
    }

    /// <summary>A cached, still-valid token serves later callers without another acquisition.</summary>
    [TestMethod]
    public async Task GetAccessTokenAsync_SecondCall_ServesFromCache()
    {
        var credential = new CountingCredential(_ => DateTimeOffset.UtcNow.AddHours(1));
        var service = Build(credential);

        var first = await service.GetAccessTokenAsync(ClusterId, TestContext.CancellationToken);
        var second = await service.GetAccessTokenAsync(ClusterId, TestContext.CancellationToken);

        Assert.AreEqual(first, second);
        Assert.AreEqual(1, credential.Calls);
    }

    /// <summary>
    /// A faulted acquisition is evicted, so the next caller retries instead of replaying the failure forever.
    /// </summary>
    [TestMethod]
    public async Task GetAccessTokenAsync_FaultedAcquisition_IsNotCached()
    {
        var credential = new CountingCredential(call =>
            call == 1 ? throw new InvalidOperationException("identity provider unavailable")
            : DateTimeOffset.UtcNow.AddHours(1));
        var service = Build(credential);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.GetAccessTokenAsync(ClusterId, TestContext.CancellationToken));

        var recovered = await service.GetAccessTokenAsync(ClusterId, TestContext.CancellationToken);

        Assert.IsNotNull(recovered);
        Assert.AreEqual(2, credential.Calls, "the failed acquisition was evicted and retried");
    }

    /// <summary>A token inside the refresh window is replaced before it is ever handed out.</summary>
    [TestMethod]
    public async Task GetAccessTokenAsync_TokenNearExpiry_IsRefreshedBeforeUse()
    {
        // The first acquisition expires inside the 5-minute refresh window, so the caller never sees it.
        var credential = new CountingCredential(call => call == 1
            ? DateTimeOffset.UtcNow.AddMinutes(1)
            : DateTimeOffset.UtcNow.AddHours(1));
        var service = Build(credential);

        var token = await service.GetAccessTokenAsync(ClusterId, TestContext.CancellationToken);

        Assert.AreEqual("token-2", token);
        Assert.AreEqual(2, credential.Calls);

        // The durable token is now cached, so a later caller does not acquire again.
        Assert.AreEqual(token, await service.GetAccessTokenAsync(ClusterId, TestContext.CancellationToken));
        Assert.AreEqual(2, credential.Calls);
    }

    /// <summary>
    /// A provider that only ever issues short-lived tokens is served rather than spun on: the second
    /// acquisition is handed out as the best available instead of looping forever.
    /// </summary>
    [TestMethod]
    public async Task GetAccessTokenAsync_AlwaysShortLived_ReturnsSecondAcquisition()
    {
        var credential = new CountingCredential(_ => DateTimeOffset.UtcNow.AddMinutes(1));
        var service = Build(credential);

        var token = await service.GetAccessTokenAsync(ClusterId, TestContext.CancellationToken);

        Assert.AreEqual("token-2", token);
        Assert.AreEqual(2, credential.Calls);
    }

    /// <summary>Builds the service with a configured token scope so the real acquisition path is used.</summary>
    private static TokenService Build(TokenCredential credential)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [$"ReverseProxy:Clusters:{ClusterId}:TokenScope"] = "api://taskflow/.default"
            })
            .Build();

        return new TokenService(NullLogger<TokenService>.Instance, credential, config);
    }

    /// <summary>Counts acquisitions and lets each test decide the expiry (or throw) per call.</summary>
    private sealed class CountingCredential(Func<int, DateTimeOffset> expiryForCall) : TokenCredential
    {
        private int _calls;

        public int Calls => _calls;

        public override async ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            // Yield so concurrent callers genuinely overlap inside the factory.
            await Task.Yield();
            return new AccessToken($"token-{call}", expiryForCall(call));
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    public TestContext TestContext { get; set; } = null!;
}
