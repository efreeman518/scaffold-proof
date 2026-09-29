using EF.Auth.Fixed;
using EF.Auth.Relay;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text.Json;
using TaskFlow.Application.Contracts;
using TaskFlow.Gateway;
using Yarp.ReverseProxy.Configuration;
using Yarp.ReverseProxy.Transforms.Builder;

namespace Test.Unit.Gateway;

/// <summary>
/// Gateway authentication and the writing side of the claims relay: the EF.Auth fixed principal authenticates every
/// request only inside <see cref="ScaffoldPrincipal.AllowedEnvironments"/>, and the EF.Gateway transforms strip any
/// inbound relay header before writing the authenticated user's own claims, under the header name the Api reads.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class GatewayAuthModeTests
{
    /// <summary>In an allowed environment every request authenticates as the Scaffold principal.</summary>
    [TestMethod]
    [DataRow("Development")]
    [DataRow("Testing")]
    [DataRow("Production")]
    public async Task ScaffoldMode_AllowedEnvironment_AuthenticatesTheFixedPrincipal(string environment)
    {
        var builder = CreateGatewayBuilder(environment);
        builder.Services.AddGatewayServices(builder.Configuration);

        await using var provider = builder.Services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };

        var result = await context.AuthenticateAsync();

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(ScaffoldPrincipal.UserId, result.Principal!.FindFirstValue("oid"));
        Assert.AreEqual(ScaffoldPrincipal.TenantId, result.Principal.FindFirstValue("tenant_id"));
        Assert.IsTrue(result.Principal.IsInRole(AppConstants.ROLE_GLOBAL_ADMIN));
    }

    /// <summary>
    /// Outside the allowed environments the scheme's options fail validation, which is what fails host start
    /// (<c>ValidateOnStart</c>), and a request that still reaches the handler throws rather than authenticating.
    /// </summary>
    [TestMethod]
    [DataRow("Staging")]
    [DataRow("QA")]
    public async Task ScaffoldMode_OtherEnvironment_NeverAuthenticates(string environment)
    {
        var builder = CreateGatewayBuilder(environment);
        builder.Services.AddGatewayServices(builder.Configuration);

        await using var provider = builder.Services.BuildServiceProvider();

        var startup = Assert.ThrowsExactly<OptionsValidationException>(() =>
            provider.GetRequiredService<IOptionsMonitor<FixedPrincipalOptions>>().Get(ScaffoldPrincipal.SchemeName));
        Assert.Contains(environment, startup.Message);

        var context = new DefaultHttpContext { RequestServices = provider };
        _ = await Assert.ThrowsExactlyAsync<OptionsValidationException>(() => context.AuthenticateAsync());
    }

    [TestMethod]
    public void UnsupportedMode_IsRejectedDuringGatewayRegistration()
    {
        var builder = CreateGatewayBuilder();
        builder.Configuration[AuthModeResolver.ConfigKey] = "Entra";

        _ = Assert.ThrowsExactly<InvalidOperationException>(
            () => builder.Services.AddGatewayServices(builder.Configuration));
    }

    [TestMethod]
    [TestCategory("Endpoint")]
    public async Task AuthModeEndpoint_IsAnonymousAndReturnsScaffold()
    {
        var builder = TestWebApplication.CreateBuilder();
        await using var app = builder.Build();
        app.MapAuthModeEndpoint(AuthMode.Scaffold);

        var endpoint = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Single(candidate => candidate.RoutePattern.RawText == "/auth/mode");

        Assert.IsNotNull(endpoint.Metadata.GetMetadata<IAllowAnonymous>());

        var context = new DefaultHttpContext
        {
            RequestServices = app.Services,
            Response = { Body = new MemoryStream() }
        };
        await endpoint.RequestDelegate!(context);

        context.Response.Body.Position = 0;
        using var payload = JsonDocument.Parse(context.Response.Body);
        Assert.AreEqual("Scaffold", payload.RootElement.GetProperty("mode").GetString());
    }

    /// <summary>
    /// Through the transforms the gateway builds for its real api route: a forged inbound relay header never reaches
    /// the Api, and the header it does send decodes, with the Api's own options, to the authenticated user's claims.
    /// </summary>
    [TestMethod]
    public async Task ClaimRelay_ReplacesForgedInboundHeaderWithTheAuthenticatedUser()
    {
        var builder = CreateGatewayBuilder();
        builder.Configuration.AddJsonFile(GatewayAppSettings, optional: false);
        builder.Services.AddGatewayServices(builder.Configuration);
        await using var provider = builder.Services.BuildServiceProvider();

        var relay = provider.GetRequiredService<IOptions<ForwardedClaimsOptions>>().Value;
        var proxyConfig = provider.GetRequiredService<IProxyConfigProvider>().GetConfig();
        var transformer = provider.GetRequiredService<ITransformBuilder>().Build(
            proxyConfig.Routes.Single(r => r.RouteId == "api-route"),
            proxyConfig.Clusters.Single(c => c.ClusterId == "api-cluster"));

        var context = new DefaultHttpContext { RequestServices = provider, User = ScaffoldUser() };
        context.Request.Headers[relay.HeaderName] = "forged";
        using var proxyRequest = new HttpRequestMessage(HttpMethod.Get, "https://api.internal/api/v1/task-items");

        await transformer.TransformRequestAsync(context, proxyRequest, "https://api.internal", TestContext.CancellationToken);

        var values = proxyRequest.Headers.GetValues(relay.HeaderName).ToArray();
        Assert.HasCount(1, values);
        Assert.AreNotEqual("forged", values[0]);

        Assert.IsTrue(ForwardedClaimsCodec.TryDecode(values[0], ApiRelayOptions(), out var claims));
        Assert.AreEqual(ScaffoldPrincipal.UserId, claims.Single(c => c.Type == "oid").Value);
        Assert.AreEqual(ScaffoldPrincipal.TenantId, claims.Single(c => c.Type == "tenant_id").Value);
        Assert.IsTrue(claims.Any(c => c.Type == ClaimTypes.Role && c.Value == AppConstants.ROLE_GLOBAL_ADMIN));
    }

    /// <summary>
    /// The relay header name and allowlist come from one <c>ForwardedClaims</c> shape in both hosts' settings, so the
    /// Api reads the header the gateway writes and keeps the tenant claim the gateway sends.
    /// </summary>
    [TestMethod]
    public void ForwardedClaims_GatewayAndApiSettings_Agree()
    {
        var gateway = Bind(GatewayAppSettings);
        var api = ApiRelayOptions();

        Assert.AreEqual(api.HeaderName, gateway.HeaderName);
        CollectionAssert.AreEquivalent(api.ClaimTypes, gateway.ClaimTypes);
        CollectionAssert.Contains(api.ClaimTypes, "tenant_id");
        Assert.IsEmpty(api.TrustedCallerIds, "the shipped Api trusts no caller: the relay is off until a gateway id is configured");
    }

    public TestContext TestContext { get; set; } = null!;

    private static string GatewayAppSettings => RepoRoot.Combine("src", "Host", "TaskFlow.Gateway", "appsettings.json");

    private static ForwardedClaimsOptions ApiRelayOptions() =>
        Bind(RepoRoot.Combine("src", "Host", "TaskFlow.Api", "appsettings.json"));

    private static ForwardedClaimsOptions Bind(string appSettings)
    {
        var config = new ConfigurationBuilder().AddJsonFile(appSettings, optional: false).Build();
        var options = new ForwardedClaimsOptions();
        config.GetSection(ForwardedClaimsOptions.ConfigSectionName).Bind(options);
        return options;
    }

    private static ClaimsPrincipal ScaffoldUser() => new(new ClaimsIdentity(
        ScaffoldPrincipal.Claims.Select(c => new Claim(c.Type, c.Value)),
        ScaffoldPrincipal.SchemeName, ClaimTypes.Name, ClaimTypes.Role));

    private static WebApplicationBuilder CreateGatewayBuilder(string? environment = null)
    {
        var builder = TestWebApplication.CreateBuilder(environment);
        builder.Configuration[AuthModeResolver.ConfigKey] = "Scaffold";
        builder.Configuration["CorsSettings:AllowedOrigins:0"] = "https://localhost";
        builder.AddServiceDefaults();
        return builder;
    }
}
