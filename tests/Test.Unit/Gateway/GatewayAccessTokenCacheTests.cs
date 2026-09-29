using Azure.Core;
using EF.Auth.Tokens;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Gateway;

namespace Test.Unit.Gateway;

/// <summary>
/// Validates the gateway's token single-flight, through the EF.Auth <see cref="AccessTokenCache"/> the gateway
/// registers (it replaced the app TokenService). Without it, a burst arriving on an expired token produces one
/// identity-provider call per request - the exact moment the provider is most likely to throttle. The cache
/// holds the in-flight acquisition, so the properties worth pinning are: one acquisition for many concurrent
/// callers, a faulted acquisition is not cached, and a token near expiry is refreshed rather than served.
/// Pure-unit tier: a counting TokenCredential is the SUT's only collaborator.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class GatewayAccessTokenCacheTests
{
    private static readonly string[] Scopes = ["api://taskflow/.default"];

    /// <summary>Fifty concurrent callers cause exactly one token acquisition.</summary>
    [TestMethod]
    public async Task GetAccessTokenAsync_FiftyConcurrentCallers_AcquiresOnce()
    {
        var credential = new CountingCredential(_ => DateTimeOffset.UtcNow.AddHours(1));
        var service = Build(credential);

        var results = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(_ => Get(service, TestContext.CancellationToken)));

        Assert.AreEqual(1, credential.Calls);
        Assert.AreEqual(1, results.Distinct().Count(), "every caller received the same token");
    }

    /// <summary>A cached, still-valid token serves later callers without another acquisition.</summary>
    [TestMethod]
    public async Task GetAccessTokenAsync_SecondCall_ServesFromCache()
    {
        var credential = new CountingCredential(_ => DateTimeOffset.UtcNow.AddHours(1));
        var service = Build(credential);

        var first = await Get(service, TestContext.CancellationToken);
        var second = await Get(service, TestContext.CancellationToken);

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
            () => Get(service, TestContext.CancellationToken));

        var recovered = await Get(service, TestContext.CancellationToken);

        Assert.IsNotNull(recovered);
        Assert.AreEqual(2, credential.Calls, "the failed acquisition was evicted and retried");
    }

    /// <summary>
    /// The caller whose request started the shared acquisition disconnects mid-flight: only that caller is
    /// cancelled. Every other waiter still receives the token, and the acquisition stays cached.
    /// </summary>
    [TestMethod]
    public async Task GetAccessTokenAsync_FirstCallerCancels_OtherWaitersStillGetToken()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var credential = new CountingCredential(_ => DateTimeOffset.UtcNow.AddHours(1), release.Task);
        var service = Build(credential);
        using var firstCallerAborted = new CancellationTokenSource();

        var first = Get(service, firstCallerAborted.Token);
        var second = Get(service, TestContext.CancellationToken);
        await firstCallerAborted.CancelAsync();
        release.SetResult();

        await Assert.ThrowsAsync<OperationCanceledException>(() => first);
        Assert.AreEqual("token-1", await second);
        Assert.AreEqual("token-1", await Get(service, TestContext.CancellationToken));
        Assert.AreEqual(1, credential.Calls, "the cancelled caller neither aborted nor evicted the shared acquisition");
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

        var token = await Get(service, TestContext.CancellationToken);

        Assert.AreEqual("token-2", token);
        Assert.AreEqual(2, credential.Calls);

        // The durable token is now cached, so a later caller does not acquire again.
        Assert.AreEqual(token, await Get(service, TestContext.CancellationToken));
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

        var token = await Get(service, TestContext.CancellationToken);

        Assert.AreEqual("token-2", token);
        Assert.AreEqual(2, credential.Calls);
    }

    /// <summary>
    /// Resolves the cache from the gateway's own registration, with the counting credential registered ahead of it so
    /// <c>AddAzureTokenCredential</c> (a try-add) leaves it in place.
    /// </summary>
    private static AccessTokenCache Build(TokenCredential credential)
    {
        var builder = TestWebApplication.CreateBuilder();
        builder.Configuration["CorsSettings:AllowedOrigins:0"] = "https://localhost";
        builder.Services.AddSingleton(credential);
        builder.Services.AddGatewayServices(builder.Configuration);
        return builder.Services.BuildServiceProvider().GetRequiredService<AccessTokenCache>();
    }

    private static async Task<string> Get(AccessTokenCache cache, CancellationToken ct) =>
        (await cache.GetTokenAsync(Scopes, ct)).Token;

    /// <summary>Counts acquisitions and lets each test decide the expiry (or throw) per call.</summary>
    private sealed class CountingCredential(Func<int, DateTimeOffset> expiryForCall, Task? gate = null) : TokenCredential
    {
        private int _calls;

        public int Calls => _calls;

        public override async ValueTask<AccessToken> GetTokenAsync(
            TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            var call = Interlocked.Increment(ref _calls);
            // Yield so concurrent callers genuinely overlap inside the factory; a gate holds the acquisition
            // open, observing the token it was given the way a real credential would.
            await Task.Yield();
            if (gate is not null) await gate.WaitAsync(cancellationToken);
            return new AccessToken($"token-{call}", expiryForCall(call));
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            GetTokenAsync(requestContext, cancellationToken).AsTask().GetAwaiter().GetResult();
    }

    public TestContext TestContext { get; set; } = null!;
}
