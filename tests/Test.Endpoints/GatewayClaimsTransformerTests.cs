using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskFlow.Api.Auth;

namespace Test.Endpoints;

/// <summary>
/// Trust boundary of the gateway claims relay: the forwarded header is honored only for the gateway's app-only
/// token, and the result is the relayed user alone - never the gateway service identity with the user's claims
/// merged in (which would attribute the request to the gateway and hand the user the gateway's app roles).
/// </summary>
[TestClass]
[TestCategory("Endpoint")]
public sealed class GatewayClaimsTransformerTests
{
    private const string GatewayAppId = "11111111-2222-3333-4444-555555555555";
    private const string HeaderName = "X-Orig-Request";

    [TestMethod]
    public async Task TrustedAppOnlyToken_YieldsOnlyTheRelayedUser()
    {
        var gateway = GatewayPrincipal(new Claim("azp", GatewayAppId));

        var result = await Transformer(GatewayAppId).TransformAsync(gateway);

        Assert.AreNotSame(gateway, result);
        Assert.AreEqual("user-oid", result.FindFirst("oid")?.Value);
        Assert.AreEqual("user-oid", result.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.AreEqual("tenant-7", result.FindFirst("tenant_id")?.Value);
        Assert.IsTrue(result.IsInRole("TenantMember"));
        Assert.IsFalse(result.IsInRole("GatewayServiceRole"), "the gateway's app roles must not reach the user");
        Assert.IsNull(result.FindFirst("azp"), "no gateway service claim is carried over");
        Assert.AreEqual(GatewayAppId, result.FindFirst(GatewayClaimsTransformSettings.RelayedByClaimType)?.Value);
        Assert.AreEqual("Test", result.Identity?.AuthenticationType);
    }

    [TestMethod]
    public async Task DelegatedUserTokenIssuedToTheGateway_IsNotTrusted()
    {
        // A user token for the gateway's client id carries its azp too; the delegated scope gives it away.
        var delegated = GatewayPrincipal(new Claim("azp", GatewayAppId), new Claim("scp", "access_as_user"));

        var result = await Transformer(GatewayAppId).TransformAsync(delegated);

        Assert.AreSame(delegated, result);
    }

    [TestMethod]
    public async Task NoGatewayAppIdConfigured_TrustsNobody()
    {
        var caller = GatewayPrincipal(new Claim("azp", GatewayAppId));

        var result = await Transformer(gatewayAppId: "").TransformAsync(caller);

        Assert.AreSame(caller, result);
    }

    [TestMethod]
    public async Task RelayedPrincipal_TransformedAgain_IsUnchanged()
    {
        var transformer = Transformer(GatewayAppId);
        var relayed = await transformer.TransformAsync(GatewayPrincipal(new Claim("azp", GatewayAppId)));

        var again = await transformer.TransformAsync(relayed);

        Assert.AreSame(relayed, again);
    }

    private static GatewayClaimsTransformer Transformer(string gatewayAppId)
    {
        var payload = JsonSerializer.Serialize(new
        {
            sub = "user-oid",
            tenant_id = "tenant-7",
            name = "Relayed User",
            roles = new[] { "TenantMember" }
        });
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Headers[HeaderName] = Convert.ToBase64String(Encoding.UTF8.GetBytes(payload));

        return new GatewayClaimsTransformer(
            NullLogger<GatewayClaimsTransformer>.Instance,
            new HttpContextAccessor { HttpContext = httpContext },
            Options.Create(new GatewayClaimsTransformSettings { HeaderName = HeaderName, GatewayAppId = gatewayAppId }));
    }

    private static ClaimsPrincipal GatewayPrincipal(params Claim[] extra) =>
        new(new ClaimsIdentity(
        [
            new Claim("oid", "gateway-service-principal"),
            new Claim(ClaimTypes.NameIdentifier, "gateway-service-principal"),
            new Claim(ClaimTypes.Role, "GatewayServiceRole"),
            .. extra
        ], "Test"));
}
