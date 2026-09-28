using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using TaskFlow.Gateway;

namespace Test.Unit.Gateway;

/// <summary>Covers gateway health check registration behavior with focused assertions that document expected behavior and regression intent.</summary>
[TestClass]
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
}
