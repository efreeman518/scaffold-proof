using System.Security.Claims;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TaskFlow.Api.RateLimiting;
using TaskFlow.Application.Contracts;

namespace Test.Endpoints;

/// <summary>
/// The Api's tenant limiter with workflow self-calls (<see cref="WorkflowRateLimitPartitioning"/>), from the Api's own
/// registration: a request relayed by a configured workflow caller is metered by the tenant's <c>workflow</c> budget in
/// its own partition, while a direct caller and a Gateway-relayed user of the same tenant keep the tenant's tier; the
/// two allowances never spend each other. With the shipped settings (no workflow caller) the package limiter is
/// unchanged, and a workflow caller the relay does not trust fails host start.
/// </summary>
[TestClass]
[TestCategory("Endpoint")]
public sealed class WorkflowRateLimitPartitionTests
{
    private const string WorkflowCaller = "aaaaaaaa-0000-0000-0000-00000000f10e";
    private const string GatewayCaller = "bbbbbbbb-0000-0000-0000-0000000000a7";
    private const string Tenant = "00000000-0000-0000-0000-0000000000b2";
    private const string Route = "/api/v1/task-items/search";

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public void WorkflowRelayedCall_GetsTheTenantsWorkflowPartition_OthersKeepTheTenantPartition()
    {
        using var factory = CreateFactory(workflowCallerConfigured: true);
        var partitioning = Partitioning(factory);

        Assert.AreEqual($"workflow:tenant:{Tenant}", partitioning.Partition(Request(factory, Relayed(WorkflowCaller))).PartitionKey);
        Assert.AreEqual($"tenant:{Tenant}", partitioning.Partition(Request(factory, Relayed(GatewayCaller))).PartitionKey,
            "a user the Gateway relays is metered by the tenant's tier");
        Assert.AreEqual($"tenant:{Tenant}", partitioning.Partition(Request(factory, Direct())).PartitionKey);
    }

    [TestMethod]
    public void ExemptPath_StaysExempt_ForAWorkflowCaller()
    {
        using var factory = CreateFactory(workflowCallerConfigured: true);
        var partitioning = Partitioning(factory);
        var partitioner = factory.Services.GetRequiredService<EF.RateLimiting.TenantRateLimitPartitioner>();
        var health = Request(factory, Relayed(WorkflowCaller), "/health/db");

        Assert.AreEqual(partitioner.Default(health).PartitionKey, partitioning.Partition(health).PartitionKey);
    }

    /// <summary>
    /// The tenant's tier allows one request and its workflow budget two, per hour: the workflow calls spend only the
    /// budget and the tenant's own request spends only the tier.
    /// </summary>
    [TestMethod]
    public void WorkflowBudget_AndTenantTier_AreSeparateAllowances()
    {
        using var factory = CreateFactory(workflowCallerConfigured: true);
        var limiter = GlobalLimiter(factory);

        Assert.IsTrue(Acquire(limiter, Request(factory, Relayed(WorkflowCaller))));
        Assert.IsTrue(Acquire(limiter, Request(factory, Relayed(WorkflowCaller))));
        Assert.IsTrue(Acquire(limiter, Request(factory, Direct())), "the workflow calls left the tenant's tier untouched");
        Assert.IsFalse(Acquire(limiter, Request(factory, Direct())), "the tier is one request");
        Assert.IsFalse(Acquire(limiter, Request(factory, Relayed(WorkflowCaller))), "the workflow budget is two requests");
    }

    /// <summary>The shipped settings list no workflow caller: a relayed call spends the tenant's tier as before.</summary>
    [TestMethod]
    public void ShippedSettings_RelayedCall_SpendsTheTenantTier()
    {
        using var factory = CreateFactory(workflowCallerConfigured: false);
        var limiter = GlobalLimiter(factory);

        Assert.IsTrue(Acquire(limiter, Request(factory, Relayed(WorkflowCaller))));
        Assert.IsFalse(Acquire(limiter, Request(factory, Direct())));
    }

    [TestMethod]
    public void WorkflowCallerNotTrustedByTheRelay_FailsHostStart()
    {
        using var factory = CreateFactory(workflowCallerConfigured: true, trustWorkflowCaller: false);

        var thrown = Assert.ThrowsExactly<InvalidOperationException>(() => GlobalLimiter(factory));
        StringAssert.Contains(thrown.Message, WorkflowCaller);
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

    /// <summary>An authenticated request, as the limiter sees it after authentication and the relay ran.</summary>
    private static DefaultHttpContext Request(WebApplicationFactory<Program> factory, ClaimsPrincipal user, string path = Route)
    {
        var context = new DefaultHttpContext { RequestServices = factory.Services, User = user };
        context.Request.Path = path;
        context.Features.Set<IAuthenticationFeature>(new AuthenticationFeature());
        return context;
    }

    /// <summary>A relayed principal as <c>EF.Auth</c> builds it: the relayed claims plus <c>ef_relayed_by</c>.</summary>
    private static ClaimsPrincipal Relayed(string caller) => new(new ClaimsIdentity(
    [
        new Claim("tenant_id", Tenant),
        new Claim(ClaimTypes.Role, AppConstants.ROLE_TENANT_MEMBER),
        new Claim("ef_relayed_by", caller),
    ], "Test"));

    private static ClaimsPrincipal Direct() => new(new ClaimsIdentity(
    [
        new Claim("tenant_id", Tenant),
        new Claim(ClaimTypes.Role, AppConstants.ROLE_TENANT_MEMBER),
    ], "Test"));

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
