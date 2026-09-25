using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TaskFlow.Gateway;
using Yarp.ReverseProxy.Configuration;

namespace Test.Unit.Gateway;

/// <summary>
/// D-064: the gateway's default request-timeout policy binds from configuration above the Api's own
/// budget, and the two routes that proxy the Api's streaming endpoints (SSE token stream, NDJSON export)
/// carry a "Disable" TimeoutPolicy so a long-held proxied connection is not cut by this default either.
/// Asserted against the gateway's real appsettings.json, same as GatewayReverseProxyHardeningTests.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class GatewayRequestTimeoutTests
{
    /// <summary>The default policy's timeout is the configured RequestTimeouts:DefaultSeconds (35 here).</summary>
    [TestMethod]
    public void AddGatewayServices_DefaultPolicy_MatchesConfiguredSeconds()
    {
        using var provider = BuildProvider();

        var options = provider.GetRequiredService<IOptions<RequestTimeoutOptions>>().Value;

        Assert.AreEqual(TimeSpan.FromSeconds(35), options.DefaultPolicy?.Timeout);
    }

    /// <summary>Both streaming routes disable the gateway's own request timeout, not just the Api's.</summary>
    [TestMethod]
    public void StreamingRoutes_DisableTheGatewayTimeout()
    {
        using var provider = BuildProvider();

        var routes = provider.GetRequiredService<IProxyConfigProvider>().GetConfig().Routes;

        AssertRouteDisablesTimeout(routes, "ai-chat-stream-route");
        AssertRouteDisablesTimeout(routes, "task-export-route");
    }

    /// <summary>The catch-all route carries no timeout override, so the default policy applies to it.</summary>
    [TestMethod]
    public void CatchAllRoute_CarriesNoTimeoutOverride()
    {
        using var provider = BuildProvider();

        var routes = provider.GetRequiredService<IProxyConfigProvider>().GetConfig().Routes;
        var route = routes.Single(r => r.RouteId == "api-route");

        Assert.IsNull(route.TimeoutPolicy);
        Assert.IsNull(route.Timeout);
    }

    private static void AssertRouteDisablesTimeout(IReadOnlyList<RouteConfig> routes, string routeId)
    {
        var route = routes.SingleOrDefault(r => r.RouteId == routeId);

        Assert.IsNotNull(route, $"route '{routeId}' was not found");
        Assert.AreEqual("Disable", route!.TimeoutPolicy, $"route '{routeId}' must disable the request timeout");
    }

    private static ServiceProvider BuildProvider()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.AddJsonFile(
            RepoRoot.Combine("src", "Host", "TaskFlow.Gateway", "appsettings.json"), optional: false);

        builder.AddServiceDefaults();
        builder.Services.AddGatewayServices(builder.Configuration);

        return builder.Services.BuildServiceProvider();
    }
}
