using EF.Common.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement;
using Microsoft.FeatureManagement.FeatureFilters;
using System.Security.Claims;
using TaskFlow.Bootstrapper;

namespace Test.Unit.Hosting;

/// <summary>
/// D-042: TenantTargetingContextAccessor feeds Microsoft.FeatureManagement's targeting filter from the
/// tenant id on IRequestContext, resolved from the current DI scope rather than a constructor
/// dependency - Gateway registers feature management on no request context at all, and the Scheduler
/// only ever has the ambient default (D-042's "else empty" case).
/// Pure-unit tier (in-memory ServiceProvider): no HTTP, no host boot.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class TenantTargetingContextAccessorTests
{
    [TestMethod]
    public async Task GetContextAsync_RequestContextRegistered_ReturnsTenantIdAsUserId()
    {
        var tenantId = Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddSingleton<IRequestContext<string, Guid?>>(
            new RequestContext<string, Guid?>("corr", "user", tenantId, []));
        using var provider = services.BuildServiceProvider();

        var accessor = new TenantTargetingContextAccessor(provider);
        var context = await accessor.GetContextAsync();

        Assert.AreEqual(tenantId.ToString(), context.UserId);
    }

    [TestMethod]
    public async Task GetContextAsync_RequestContextTenantIdNull_ReturnsEmpty()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IRequestContext<string, Guid?>>(
            new RequestContext<string, Guid?>("corr", "user", null, []));
        using var provider = services.BuildServiceProvider();

        var accessor = new TenantTargetingContextAccessor(provider);
        var context = await accessor.GetContextAsync();

        Assert.AreEqual(string.Empty, context.UserId);
    }

    /// <summary>
    /// The real registration: WithTargeting makes the accessor a singleton built on the root provider while the
    /// request context is scoped. Two sequential requests from different tenants must each be targeted as
    /// themselves; resolving the request context from the root provider would pin every request to the first one.
    /// </summary>
    [TestMethod]
    public async Task GetContextAsync_SequentialRequestsFromDifferentTenants_TargetsEachTenant()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["FeatureManagement:Pilot:EnabledFor:0:Name"] = "Microsoft.Targeting",
                ["FeatureManagement:Pilot:EnabledFor:0:Parameters:Audience:Users:0"] = tenantA.ToString(),
                ["FeatureManagement:Pilot:EnabledFor:0:Parameters:Audience:DefaultRolloutPercentage"] = "0"
            })
            .Build());
        services.AddLogging();
        RegisterServices.AddRequestContext(services);
        services.AddTaskFlowFeatureManagement();
        using var provider = services.BuildServiceProvider();
        var accessor = provider.GetRequiredService<ITargetingContextAccessor>();
        var features = provider.GetRequiredService<IVariantFeatureManager>();

        var (targetedA, enabledA) = await InRequestAsync(provider, tenantA, accessor, features);
        var (targetedB, enabledB) = await InRequestAsync(provider, tenantB, accessor, features);

        Assert.AreEqual(tenantA.ToString(), targetedA);
        Assert.AreEqual(tenantB.ToString(), targetedB);
        Assert.IsTrue(enabledA, "the targeted tenant gets the flag");
        Assert.IsFalse(enabledB, "a later tenant must not inherit the first request's targeting");
    }

    [TestMethod]
    public async Task GetContextAsync_NoRequestContextRegistered_ReturnsEmpty()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var accessor = new TenantTargetingContextAccessor(provider);
        var context = await accessor.GetContextAsync();

        Assert.AreEqual(string.Empty, context.UserId);
    }

    /// <summary>Runs one simulated HTTP request for <paramref name="tenantId"/> with its own request scope.</summary>
    private static async Task<(string? TargetedAs, bool PilotEnabled)> InRequestAsync(
        ServiceProvider provider, Guid tenantId, ITargetingContextAccessor accessor, IVariantFeatureManager features)
    {
        using var scope = provider.CreateScope();
        var httpContextAccessor = provider.GetRequiredService<IHttpContextAccessor>();
        httpContextAccessor.HttpContext = new DefaultHttpContext
        {
            RequestServices = scope.ServiceProvider,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("oid", $"user-{tenantId:N}"), new Claim("tenant_id", tenantId.ToString())], "Test"))
        };
        try
        {
            var targeting = await accessor.GetContextAsync();
            return (targeting.UserId, await features.IsEnabledAsync("Pilot"));
        }
        finally
        {
            httpContextAccessor.HttpContext = null;
        }
    }
}
