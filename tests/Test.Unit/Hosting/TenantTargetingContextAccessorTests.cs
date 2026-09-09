using EF.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;
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

    [TestMethod]
    public async Task GetContextAsync_NoRequestContextRegistered_ReturnsEmpty()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        var accessor = new TenantTargetingContextAccessor(provider);
        var context = await accessor.GetContextAsync();

        Assert.AreEqual(string.Empty, context.UserId);
    }
}
