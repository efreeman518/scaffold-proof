using System.Net;
using System.Security.Claims;
using EF.Auth.Fixed;
using EF.Auth.Relay;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.Internal;
using Microsoft.Extensions.Options;
using TaskFlow.Api.Auth;
using TaskFlow.Application.Contracts;

namespace Test.Endpoints;

/// <summary>
/// Api side of the authentication contract, through the Api's own registration (EF.Auth):
/// <list type="bullet">
/// <item>the claims relay honors the forwarded header only for an app-only token from a configured trusted gateway,
/// and the result is the relayed user alone - never the gateway service identity with the user's claims merged in
/// (which would attribute the request to the gateway and hand the user the gateway's app roles);</item>
/// <item>a trusted app-only caller without a relay header is unauthenticated (<c>RequireHeaderFromTrustedCaller</c>, the
/// package default), so an endpoint requiring an authenticated user answers 403 instead of running as the gateway;</item>
/// <item>the Scaffold fixed principal cannot authenticate outside <see cref="ScaffoldPrincipal.AllowedEnvironments"/>.</item>
/// </list>
/// </summary>
[TestClass]
[TestCategory("Endpoint")]
public sealed class ForwardedClaimsRelayTests
{
    private const string GatewayAppId = "11111111-2222-3333-4444-555555555555";

    [TestMethod]
    public async Task TrustedAppOnlyToken_YieldsOnlyTheRelayedUser()
    {
        using var factory = CreateFactory(GatewayAppId);
        var gateway = GatewayPrincipal(new Claim("azp", GatewayAppId));

        var result = await Transform(factory, gateway);

        Assert.AreNotSame(gateway, result);
        Assert.AreEqual("user-oid", result.FindFirst("oid")?.Value);
        Assert.AreEqual("tenant-7", result.FindFirst("tenant_id")?.Value);
        Assert.IsTrue(result.IsInRole("TenantMember"));
        Assert.IsFalse(result.IsInRole("GatewayServiceRole"), "the gateway's app roles must not reach the user");
        Assert.IsNull(result.FindFirst("azp"), "no gateway service claim is carried over");
        Assert.IsNull(result.FindFirst("smuggled"), "a claim type outside the allowlist is dropped");
        Assert.IsFalse(
            result.Claims.Any(c => c.Value == "gateway-service-principal"),
            "no claim of the gateway identity is copied");
        Assert.AreEqual(GatewayAppId, result.FindFirst("ef_relayed_by")?.Value);
        Assert.AreEqual("Test", result.Identity?.AuthenticationType);
    }

    [TestMethod]
    public async Task DelegatedUserTokenIssuedToTheGateway_IsNotTrusted()
    {
        using var factory = CreateFactory(GatewayAppId);
        // A user token for the gateway's client id carries its azp too; the delegated scope gives it away.
        var delegated = GatewayPrincipal(new Claim("azp", GatewayAppId), new Claim("scp", "access_as_user"));

        Assert.AreSame(delegated, await Transform(factory, delegated));
    }

    [TestMethod]
    public async Task UnlistedCaller_IsNotTrusted()
    {
        using var factory = CreateFactory(GatewayAppId);
        var other = GatewayPrincipal(new Claim("azp", "99999999-0000-0000-0000-000000000000"));

        Assert.AreSame(other, await Transform(factory, other));
    }

    /// <summary>The shipped Api settings list no trusted caller, so the relay trusts nobody.</summary>
    [TestMethod]
    public async Task ShippedSettings_TrustNobody()
    {
        using var factory = CreateFactory(trustedCallerId: null);
        var caller = GatewayPrincipal(new Claim("azp", GatewayAppId));

        Assert.AreSame(caller, await Transform(factory, caller));
    }

    [TestMethod]
    public async Task RelayedPrincipal_TransformedAgain_IsUnchanged()
    {
        using var factory = CreateFactory(GatewayAppId);
        var relayed = await Transform(factory, GatewayPrincipal(new Claim("azp", GatewayAppId)));

        Assert.AreSame(relayed, await Transform(factory, relayed));
    }

    /// <summary>
    /// The request reaches the real pipeline authenticated as the trusted gateway's app-only identity but carries no
    /// relay header: the transformation leaves an unauthenticated principal, and the authenticated-user fallback
    /// policy forbids it rather than running it as the gateway service principal with its app roles.
    /// </summary>
    [TestMethod]
    public async Task TrustedAppOnlyCaller_WithoutRelayHeader_Gets403()
    {
        using var factory = TrustedGatewayCallerFactory();
        using var client = factory.CreateClient();

        // A real route, not the fallback policy: with RequireHeaderFromTrustedCaller=false the same request runs as the
        // gateway service principal and is not forbidden (checked while writing this test).
        using var response = await client.GetAsync($"/api/v1/task-items/{Guid.CreateVersion7()}", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The Gateway's aggregate health check calls the authorized <c>/health/full</c> with its own app-only token and no
    /// relay header. <c>ForwardedClaims:ServicePathPrefixes = ["/health"]</c> in the shipped Api settings keeps the
    /// trusted caller's own principal on that path only, so the probe is answered instead of forbidden.
    /// </summary>
    [TestMethod]
    public async Task TrustedAppOnlyCaller_WithoutRelayHeader_IsAnsweredOnTheServiceHealthPath()
    {
        using var factory = TrustedGatewayCallerFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health/full", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    public TestContext TestContext { get; set; } = null!;

    /// <summary>The Api's fixed principal authenticates in an allowed environment and nowhere else.</summary>
    [TestMethod]
    [DataRow("Development", true)]
    [DataRow("Testing", true)]
    [DataRow("Production", true)]
    [DataRow("Staging", false)]
    [DataRow("QA", false)]
    public async Task ScaffoldPrincipal_AuthenticatesOnlyInAllowedEnvironments(string environment, bool allowed)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new HostingEnvironment { EnvironmentName = environment });
        services.AddTaskFlowAuth(new ConfigurationBuilder().Build());
        await using var provider = services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };

        if (!allowed)
        {
            _ = Assert.ThrowsExactly<OptionsValidationException>(() =>
                provider.GetRequiredService<IOptionsMonitor<FixedPrincipalOptions>>().Get(ScaffoldPrincipal.SchemeName));
            _ = await Assert.ThrowsExactlyAsync<OptionsValidationException>(() => context.AuthenticateAsync());
            return;
        }

        var result = await context.AuthenticateAsync();
        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(ScaffoldPrincipal.UserId, result.Principal!.FindFirst("oid")?.Value);
        Assert.AreEqual(ScaffoldPrincipal.TenantId, result.Principal.FindFirst("tenant_id")?.Value);
    }

    /// <summary>Runs the Api's registered claims transformation for a request carrying a relay header.</summary>
    private static Task<ClaimsPrincipal> Transform(WebApplicationFactory<Program> factory, ClaimsPrincipal principal)
    {
        var services = factory.Services;
        var relay = services.GetRequiredService<IOptions<ForwardedClaimsOptions>>().Value;
        var header = ForwardedClaimsCodec.Encode(RelayedUser(), new ForwardedClaimsOptions
        {
            ClaimTypes = [.. relay.ClaimTypes!, "smuggled"]
        });

        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Headers[relay.HeaderName] = header;
        services.GetRequiredService<IHttpContextAccessor>().HttpContext = httpContext;

        return services.GetRequiredService<IClaimsTransformation>().TransformAsync(principal);
    }

    /// <summary>The Api with the gateway trusted, every request authenticated as the gateway's app-only identity.</summary>
    private static WebApplicationFactory<Program> TrustedGatewayCallerFactory() =>
        CreateFactory(GatewayAppId).WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
                services.PostConfigure<FixedPrincipalOptions>(ScaffoldPrincipal.SchemeName, options => options.Claims =
                [
                    new FixedClaim("oid", "gateway-service-principal"),
                    new FixedClaim("azp", GatewayAppId),
                    new FixedClaim(ClaimTypes.Role, "GatewayServiceRole")
                ])));

    private static WebApplicationFactory<Program> CreateFactory(string? trustedCallerId) =>
        new CustomApiFactory().WithWebHostBuilder(builder =>
        {
            if (trustedCallerId is not null)
                builder.UseSetting("ForwardedClaims:TrustedCallerIds:0", trustedCallerId);
        });

    private static ClaimsPrincipal RelayedUser() => new(new ClaimsIdentity(
    [
        new Claim("oid", "user-oid"),
        new Claim("tenant_id", "tenant-7"),
        new Claim(ClaimTypes.Role, "TenantMember"),
        new Claim("smuggled", "x")
    ], "Gateway"));

    private static ClaimsPrincipal GatewayPrincipal(params Claim[] extra) =>
        new(new ClaimsIdentity(
        [
            new Claim("oid", "gateway-service-principal"),
            new Claim(ClaimTypes.NameIdentifier, "gateway-service-principal"),
            new Claim(ClaimTypes.Role, "GatewayServiceRole"),
            .. extra
        ], "Test"));
}
