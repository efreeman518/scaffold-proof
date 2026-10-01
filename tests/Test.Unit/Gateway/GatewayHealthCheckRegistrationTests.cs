using EF.Gateway;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TaskFlow.Gateway;

namespace Test.Unit.Gateway;

/// <summary>
/// The gateway's Api probe is the EF.Gateway <see cref="DownstreamHealthCheck"/> (S22), registered from the
/// gateway's real settings; a missing health URL fails startup validation instead of reporting a skipped probe.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class GatewayHealthCheckRegistrationTests
{
    /// <summary>Verifies add gateway services with service defaults resolves health check service behavior and protects the expected test contract.</summary>
    [TestMethod]
    public void AddGatewayServices_WithServiceDefaults_ResolvesHealthCheckService()
    {
        var builder = TestWebApplication.CreateBuilder();
        builder.Configuration["CorsSettings:AllowedOrigins:0"] = "https://localhost";

        builder.AddServiceDefaults();
        builder.Services.AddGatewayServices(builder.Configuration);

        using var provider = builder.Services.BuildServiceProvider();

        _ = provider.GetRequiredService<HealthCheckService>();
    }

    /// <summary>The shipped settings register the downstream probe at the Api's full health URL, tagged full.</summary>
    [TestMethod]
    public void ApiProbe_FromGatewaySettings_IsTheDownstreamHealthCheck()
    {
        var builder = TestWebApplication.CreateBuilder();
        builder.Configuration.AddJsonFile(
            RepoRoot.Combine("src", "Host", "TaskFlow.Gateway", "appsettings.json"), optional: false);
        builder.Services.AddGatewayServices(builder.Configuration);
        using var provider = builder.Services.BuildServiceProvider();

        var registration = provider.GetRequiredService<IOptions<HealthCheckServiceOptions>>().Value.Registrations
            .Single(r => r.Name == "taskflow-api");
        var options = provider.GetRequiredService<IOptionsMonitor<DownstreamHealthCheckOptions>>().Get("taskflow-api");

        Assert.IsInstanceOfType<DownstreamHealthCheck>(registration.Factory(provider));
        CollectionAssert.IsSubsetOf(new[] { "full", "extservice" }, registration.Tags.ToArray());
        Assert.AreEqual(new Uri("https+http://taskflowapi/health/full"), options.Url);
        Assert.IsTrue(string.IsNullOrEmpty(options.TokenScope), "Scaffold mode sends no downstream token");
    }

    /// <summary>No health URL is a startup failure (ValidateOnStart), not a silently healthy probe.</summary>
    [TestMethod]
    public void ApiProbe_WithoutUrl_FailsValidation()
    {
        var builder = TestWebApplication.CreateBuilder();
        builder.Configuration["CorsSettings:AllowedOrigins:0"] = "https://localhost";
        builder.Services.AddGatewayServices(builder.Configuration);
        using var provider = builder.Services.BuildServiceProvider();

        _ = Assert.ThrowsExactly<OptionsValidationException>(
            () => provider.GetRequiredService<IOptionsMonitor<DownstreamHealthCheckOptions>>().Get("taskflow-api"));
    }
}
