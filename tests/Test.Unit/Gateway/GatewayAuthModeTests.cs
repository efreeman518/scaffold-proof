using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using TaskFlow.Application.Contracts;
using TaskFlow.Gateway;

namespace Test.Unit.Gateway;

[TestClass]
[TestCategory("Unit")]
public sealed class GatewayAuthModeTests
{
    [TestMethod]
    public async Task ScaffoldMode_RegistersAutomaticPrincipal()
    {
        var builder = CreateGatewayBuilder();
        builder.Services.AddGatewayServices(builder.Configuration);

        await using var provider = builder.Services.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = provider };

        var result = await context.AuthenticateAsync();

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(ScaffoldAuthHandler.ScaffoldUserId, result.Principal!.FindFirstValue("oid"));
        Assert.AreEqual(
            ScaffoldAuthHandler.ScaffoldTenantId,
            result.Principal.FindFirstValue("tenant_id"));
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

    [TestMethod]
    public void ClaimRelay_ReplacesForgedInboundHeader()
    {
        using var request = new HttpRequestMessage();
        request.Headers.TryAddWithoutValidation("X-Orig-Request", "forged");
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("oid", ScaffoldAuthHandler.ScaffoldUserId),
            new Claim("tenant_id", ScaffoldAuthHandler.ScaffoldTenantId),
            new Claim(ClaimTypes.Name, "Scaffold Principal"),
            new Claim(ClaimTypes.Role, "GlobalAdmin")
        ], ScaffoldAuthHandler.SchemeName));

        RegisterGatewayServices.ReplaceOriginalUserClaimsHeader(request, principal);

        var values = request.Headers.GetValues("X-Orig-Request").ToArray();
        Assert.HasCount(1, values);
        var value = values[0];
        Assert.AreNotEqual("forged", value);
        using var payload = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(value)));
        Assert.AreEqual(
            ScaffoldAuthHandler.ScaffoldUserId,
            payload.RootElement.GetProperty("sub").GetString());
        Assert.AreEqual(
            ScaffoldAuthHandler.ScaffoldTenantId,
            payload.RootElement.GetProperty("tenant_id").GetString());
    }

    private static WebApplicationBuilder CreateGatewayBuilder()
    {
        var builder = TestWebApplication.CreateBuilder();
        builder.Configuration[AuthModeResolver.ConfigKey] = "Scaffold";
        builder.Configuration["CorsSettings:AllowedOrigins:0"] = "https://localhost";
        builder.AddServiceDefaults();
        return builder;
    }
}
