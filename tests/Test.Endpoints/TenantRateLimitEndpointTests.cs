using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using TaskFlow.Api;
using TaskFlow.Application.Contracts;

namespace Test.Endpoints;

/// <summary>
/// Guards the tenant rate-limit contract end to end through the real pipeline: the budget partitions on the
/// caller's tenant claim (so the limiter must run after authentication), each request spends one permit of
/// one budget, and the streaming export spends only its own Export budget.
/// </summary>
[TestClass]
[TestCategory("Endpoint")]
public sealed class TenantRateLimitEndpointTests
{
    private const string InteractiveRoute = "/api/v1/task-items/summary";

    /// <summary>
    /// The scaffold tenant is assigned the tight "free" tier while every other partition gets "premium". A 429
    /// on the third call proves the partition key was the tenant claim; a limiter ahead of authentication
    /// would see an anonymous caller, fall back to the premium default, and never reject.
    /// </summary>
    [TestMethod]
    public async Task Given_TenantOnTightTier_When_BudgetSpent_Then_Returns429()
    {
        using var factory = CreateFactory(tenantPermitLimit: 2);
        using var client = factory.CreateClient();

        using var first = await client.GetAsync(InteractiveRoute, TestContext.CancellationToken);
        using var second = await client.GetAsync(InteractiveRoute, TestContext.CancellationToken);
        using var third = await client.GetAsync(InteractiveRoute, TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, third.StatusCode);
    }

    /// <summary>
    /// Exports spend the Export budget only: with a one-permit interactive budget, two exports still succeed
    /// and the tenant's single interactive permit is still available afterwards.
    /// </summary>
    [TestMethod]
    public async Task Given_Exports_When_InteractiveCallFollows_Then_InteractiveBudgetUntouched()
    {
        using var factory = CreateFactory(tenantPermitLimit: 1);
        using var client = factory.CreateClient();

        using var export1 = await client.GetAsync("/api/v1/task-items/export", TestContext.CancellationToken);
        using var export2 = await client.GetAsync("/api/v1/task-items/export", TestContext.CancellationToken);
        using var interactive = await client.GetAsync(InteractiveRoute, TestContext.CancellationToken);
        using var overBudget = await client.GetAsync(InteractiveRoute, TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, export1.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, export2.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, interactive.StatusCode);
        Assert.AreEqual(HttpStatusCode.TooManyRequests, overBudget.StatusCode);
    }

    private static WebApplicationFactory<Program> CreateFactory(int tenantPermitLimit) =>
        new CustomApiFactory().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("RateLimiting:Tenants:DefaultTier", "premium");
            builder.UseSetting($"RateLimiting:Tenants:TenantTiers:{ScaffoldPrincipal.TenantId}", "free");
            builder.UseSetting("RateLimiting:Tenants:Tiers:free:PermitLimit", tenantPermitLimit.ToString(System.Globalization.CultureInfo.InvariantCulture));
            builder.UseSetting("RateLimiting:Tenants:Tiers:free:WindowSeconds", "3600");
            builder.UseSetting("RateLimiting:Tenants:Budgets:export:PermitLimit", "5");
            builder.UseSetting("RateLimiting:Tenants:Budgets:export:WindowSeconds", "3600");
        });

    public TestContext TestContext { get; set; } = null!;
}
