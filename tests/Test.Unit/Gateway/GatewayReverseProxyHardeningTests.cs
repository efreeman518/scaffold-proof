using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using TaskFlow.Gateway;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Health;

namespace Test.Unit.Gateway;

/// <summary>
/// D-050 YARP hardening, asserted against the gateway's real appsettings.json rather than a copy of it.
/// The point is that the settings actually bind: YARP's configuration binder ignores a key it does not
/// recognise, so a typo in a cluster section leaves the cluster silently unhardened and no other test in the
/// suite would notice. The active health check services must also be present, since an active health section
/// with no monitor registered is the same silent no-op.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class GatewayReverseProxyHardeningTests
{
    /// <summary>Active health checking requires the monitor AddReverseProxy is expected to register.</summary>
    [TestMethod]
    public void AddGatewayServices_RegistersTheActiveHealthCheckMonitor()
    {
        using var provider = BuildProvider();

        _ = provider.GetRequiredService<IActiveHealthCheckMonitor>();
    }

    /// <summary>Every hardening knob binds to the YARP model with the value the settings file states.</summary>
    [TestMethod]
    public void ApiCluster_BindsActivePassiveHealthLoadBalancingAndHttpRequestSettings()
    {
        using var provider = BuildProvider();

        var cluster = provider.GetRequiredService<IProxyConfigProvider>()
            .GetConfig().Clusters.Single(c => c.ClusterId == "api-cluster");

        Assert.AreEqual("PowerOfTwoChoices", cluster.LoadBalancingPolicy);

        var active = cluster.HealthCheck?.Active;
        Assert.IsNotNull(active, "the active health section did not bind");
        Assert.IsTrue(active.Enabled);
        Assert.AreEqual("/healthz/ready", active.Path);
        Assert.AreEqual("ConsecutiveFailures", active.Policy);
        Assert.AreEqual(TimeSpan.FromSeconds(10), active.Interval);
        Assert.AreEqual(TimeSpan.FromSeconds(5), active.Timeout);
        Assert.AreEqual("3", cluster.Metadata?["ConsecutiveFailuresHealthPolicy.Threshold"]);

        var passive = cluster.HealthCheck?.Passive;
        Assert.IsNotNull(passive, "the passive health section did not bind");
        Assert.IsTrue(passive.Enabled);
        Assert.AreEqual("TransportFailureRate", passive.Policy);
        Assert.AreEqual(TimeSpan.FromSeconds(30), passive.ReactivationPeriod);

        var request = cluster.HttpRequest;
        Assert.IsNotNull(request, "the HttpRequest section did not bind");
        Assert.AreEqual(TimeSpan.FromSeconds(30), request.ActivityTimeout);
        Assert.AreEqual(HttpVersion.Version20, request.Version);
        Assert.AreEqual(HttpVersionPolicy.RequestVersionOrLower, request.VersionPolicy);
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
