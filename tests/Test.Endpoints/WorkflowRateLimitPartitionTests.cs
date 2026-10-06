using System.Net;
using System.Security.Claims;
using System.Threading.RateLimiting;
using EF.Auth.Fixed;
using EF.Auth.Relay;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TaskFlow.Api.RateLimiting;
using TaskFlow.Application.Contracts;

namespace Test.Endpoints;

/// <summary>
/// The Api's tenant limiter with workflow self-calls (<see cref="WorkflowRateLimitPartitioning"/>), from the Api's own
/// registration: a request the relay built from a configured workflow caller's header is metered by the tenant's
/// <c>workflow</c> budget in its own partition, while a direct caller, a Gateway-relayed user and a token that merely
/// carries a relayed-by claim keep the tenant's tier; the two allowances never spend each other. With the shipped
/// settings (no workflow caller) the package limiter is unchanged, and a workflow caller the relay does not trust or a
/// disabled global limiter fails host start.
/// </summary>
[TestClass]
[TestCategory("Endpoint")]
public sealed class WorkflowRateLimitPartitionTests
{
    private const string WorkflowCaller = "aaaaaaaa-0000-0000-0000-00000000f10e";
    private const string GatewayCaller = "bbbbbbbb-0000-0000-0000-0000000000a7";
    private const string Tenant = "00000000-0000-0000-0000-0000000000b2";
    private const string Route = "/api/v1/task-items/search";
    private const string InteractiveRoute = "/api/v1/task-items/summary";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task WorkflowRelayedCall_GetsTheTenantsWorkflowPartition_OthersKeepTheTenantPartition()
    {
        using var factory = CreateFactory(workflowCallerConfigured: true);
        var partitioning = Partitioning(factory);

        Assert.AreEqual($"workflow:tenant:{Tenant}", partitioning.Partition(await RelayedRequest(factory, WorkflowCaller)).PartitionKey);
        Assert.AreEqual($"tenant:{Tenant}", partitioning.Partition(await RelayedRequest(factory, GatewayCaller)).PartitionKey,
            "a user the Gateway relays is metered by the tenant's tier");
        Assert.AreEqual($"tenant:{Tenant}", partitioning.Partition(Request(factory, Direct())).PartitionKey);
    }

    /// <summary>
    /// A token whose issuer put a relayed-by claim naming the workflow caller on it is not a relayed principal: the relay
    /// returns it unchanged, so it stays on the tenant's tier.
    /// </summary>
    [TestMethod]
    public void TokenCarryingARelayedByClaim_NotBuiltByTheRelay_StaysOnTheTenantTier()
    {
        using var factory = CreateFactory(workflowCallerConfigured: true);
        var forged = Direct(new Claim("ef_relayed_by", WorkflowCaller));

        Assert.AreEqual($"tenant:{Tenant}", Partitioning(factory).Partition(Request(factory, forged)).PartitionKey);
    }

    [TestMethod]
    public async Task ExemptPath_StaysExempt_ForAWorkflowCaller()
    {
        using var factory = CreateFactory(workflowCallerConfigured: true);
        var partitioner = factory.Services.GetRequiredService<EF.RateLimiting.TenantRateLimitPartitioner>();
        var health = await RelayedRequest(factory, WorkflowCaller, "/health/db");

        Assert.AreEqual(partitioner.Default(health).PartitionKey, Partitioning(factory).Partition(health).PartitionKey);
    }

    /// <summary>
    /// The tenant's tier allows one request and its workflow budget two, per hour: the workflow calls spend only the
    /// budget and the tenant's own request spends only the tier.
    /// </summary>
    [TestMethod]
    public async Task WorkflowBudget_AndTenantTier_AreSeparateAllowances()
    {
        using var factory = CreateFactory(workflowCallerConfigured: true);
        var limiter = GlobalLimiter(factory);

        Assert.IsTrue(Acquire(limiter, await RelayedRequest(factory, WorkflowCaller)));
        Assert.IsTrue(Acquire(limiter, await RelayedRequest(factory, WorkflowCaller)));
        Assert.IsTrue(Acquire(limiter, Request(factory, Direct())), "the workflow calls left the tenant's tier untouched");
        Assert.IsFalse(Acquire(limiter, Request(factory, Direct())), "the tier is one request");
        Assert.IsFalse(Acquire(limiter, await RelayedRequest(factory, WorkflowCaller)), "the workflow budget is two requests");
    }

    /// <summary>
    /// Through the real pipeline: the workflow caller's app-only identity with a relay header spends the one-request
    /// workflow budget (the second request is 429 while the tier allows 100), and an identity that carries a relayed-by
    /// claim of its own does not (both requests answered within the tier).
    /// </summary>
    [TestMethod]
    public async Task Pipeline_RelayedWorkflowCall_SpendsTheBudget_ATokenClaimingItDoesNot()
    {
        using (var relayed = PipelineFactory([new FixedClaim("oid", "workflow-host"), new FixedClaim("azp", WorkflowCaller)]))
        {
            using var client = relayed.CreateClient();
            client.DefaultRequestHeaders.Add(RelayOptions(relayed).HeaderName, Header(relayed));
            using var first = await client.GetAsync(InteractiveRoute, TestContext.CancellationToken);
            using var second = await client.GetAsync(InteractiveRoute, TestContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
            Assert.AreEqual(HttpStatusCode.TooManyRequests, second.StatusCode, "the workflow budget is one request");
        }

        using var claimed = PipelineFactory(
        [
            new FixedClaim("oid", "user"), new FixedClaim("tenant_id", Tenant),
            new FixedClaim(ClaimTypes.Role, AppConstants.ROLE_TENANT_MEMBER), new FixedClaim("ef_relayed_by", WorkflowCaller),
        ]);
        using var claimedClient = claimed.CreateClient();
        using var one = await claimedClient.GetAsync(InteractiveRoute, TestContext.CancellationToken);
        using var two = await claimedClient.GetAsync(InteractiveRoute, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, one.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, two.StatusCode, "a claimed relayed-by is metered by the tenant's tier");
    }

    /// <summary>The shipped settings list no workflow caller: a relayed call spends the tenant's tier as before.</summary>
    [TestMethod]
    public async Task ShippedSettings_RelayedCall_SpendsTheTenantTier()
    {
        using var factory = CreateFactory(workflowCallerConfigured: false);
        var limiter = GlobalLimiter(factory);

        Assert.IsTrue(Acquire(limiter, await RelayedRequest(factory, WorkflowCaller)));
        Assert.IsFalse(Acquire(limiter, Request(factory, Direct())));
    }

    [TestMethod]
    public void WorkflowCallerNotTrustedByTheRelay_FailsHostStart()
    {
        using var factory = CreateFactory(workflowCallerConfigured: true, trustWorkflowCaller: false);

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => GlobalLimiter(factory));
        StringAssert.Contains(thrown.Message, WorkflowCaller);
    }

    [TestMethod]
    public void WorkflowCallerWithTheGlobalLimiterOff_FailsHostStart()
    {
        using var factory = CreateFactory(workflowCallerConfigured: true).WithWebHostBuilder(builder =>
            builder.UseSetting("RateLimiting:Tenants:UseGlobalLimiter", "false"));

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(
            () => factory.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value);
        StringAssert.Contains(thrown.Message, "UseGlobalLimiter");
    }

    private static bool Acquire(PartitionedRateLimiter<HttpContext> limiter, HttpContext context)
    {
        using var lease = limiter.AttemptAcquire(context);
        return lease.IsAcquired;
    }

    private static PartitionedRateLimiter<HttpContext> GlobalLimiter(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter
        ?? throw new AssertFailedException("the tenant limiter installs a global limiter");

    private static WorkflowRateLimitPartitioning Partitioning(WebApplicationFactory<Program> factory) =>
        factory.Services.GetServices<IPostConfigureOptions<RateLimiterOptions>>().OfType<WorkflowRateLimitPartitioning>().Single();

    private static ForwardedClaimsOptions RelayOptions(WebApplicationFactory<Program> factory) =>
        factory.Services.GetRequiredService<IOptions<ForwardedClaimsOptions>>().Value;

    /// <summary>A relay header for the tenant's member, as a relaying caller writes it.</summary>
    private static string Header(WebApplicationFactory<Program> factory) =>
        ForwardedClaimsCodec.Encode(Direct(), RelayOptions(factory));

    /// <summary>An authenticated request, as the limiter sees it after authentication ran.</summary>
    private static DefaultHttpContext Request(WebApplicationFactory<Program> factory, ClaimsPrincipal user, string path = Route)
    {
        var context = new DefaultHttpContext { RequestServices = factory.Services, User = user };
        context.Request.Path = path;
        context.Features.Set<IAuthenticationFeature>(new AuthenticationFeature());
        return context;
    }

    /// <summary>
    /// A request from <paramref name="caller"/>'s app-only identity with a relay header, its user the principal the
    /// Api's registered claims transformation returns.
    /// </summary>
    private static async Task<DefaultHttpContext> RelayedRequest(WebApplicationFactory<Program> factory, string caller, string path = Route)
    {
        var context = Request(factory, new ClaimsPrincipal(new ClaimsIdentity([new Claim("azp", caller)], "Test")), path);
        context.Request.Headers[RelayOptions(factory).HeaderName] = Header(factory);
        factory.Services.GetRequiredService<IHttpContextAccessor>().HttpContext = context;
        context.User = await factory.Services.GetRequiredService<IClaimsTransformation>().TransformAsync(context.User);
        Assert.AreEqual(caller, context.User.FindFirst("ef_relayed_by")?.Value, "the relay built the principal");
        return context;
    }

    private static ClaimsPrincipal Direct(params Claim[] extra) => new(new ClaimsIdentity(
    [
        new Claim("tenant_id", Tenant),
        new Claim(ClaimTypes.Role, AppConstants.ROLE_TENANT_MEMBER),
        .. extra,
    ], "Test"));

    /// <summary>Every request authenticates as <paramref name="claims"/>; a one-request workflow budget, a 100-request tier.</summary>
    private static WebApplicationFactory<Program> PipelineFactory(FixedClaim[] claims) =>
        new CustomApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ForwardedClaims:TrustedCallerIds:0", WorkflowCaller);
            builder.UseSetting("RateLimiting:Workflow:CallerIds:0", WorkflowCaller);
            builder.UseSetting($"RateLimiting:Tenants:TenantTiers:{Tenant}", "free");
            builder.UseSetting("RateLimiting:Tenants:Tiers:free:PermitLimit", "100");
            builder.UseSetting("RateLimiting:Tenants:Tiers:free:WindowSeconds", "3600");
            builder.UseSetting("RateLimiting:Tenants:Budgets:workflow:PermitLimit", "1");
            builder.UseSetting("RateLimiting:Tenants:Budgets:workflow:WindowSeconds", "3600");
            builder.ConfigureTestServices(services =>
                services.PostConfigure<FixedPrincipalOptions>(ScaffoldPrincipal.SchemeName, options => options.Claims = [.. claims]));
        });

    private static WebApplicationFactory<Program> CreateFactory(bool workflowCallerConfigured, bool trustWorkflowCaller = true) =>
        new CustomApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ForwardedClaims:TrustedCallerIds:0", GatewayCaller);
            if (trustWorkflowCaller)
                builder.UseSetting("ForwardedClaims:TrustedCallerIds:1", WorkflowCaller);
            if (workflowCallerConfigured)
                builder.UseSetting("RateLimiting:Workflow:CallerIds:0", WorkflowCaller);
            builder.UseSetting($"RateLimiting:Tenants:TenantTiers:{Tenant}", "free");
            builder.UseSetting("RateLimiting:Tenants:Tiers:free:PermitLimit", "1");
            builder.UseSetting("RateLimiting:Tenants:Tiers:free:WindowSeconds", "3600");
            builder.UseSetting("RateLimiting:Tenants:Budgets:workflow:PermitLimit", "2");
            builder.UseSetting("RateLimiting:Tenants:Budgets:workflow:WindowSeconds", "3600");
        });
}
