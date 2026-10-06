using System.Security.Claims;
using System.Text.Encodings.Web;
using Azure.Core;
using EF.Auth.Relay;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskFlow.Application.Contracts;

namespace Test.Integration;

/// <summary>
/// TEST-ONLY identity for the workflow self-call relay (D-068). TaskFlow ships the Scaffold fixed principal alone, and a
/// live deployment brings its own token scheme, so this stands in for that scheme in an Api test host:
/// <list type="bullet">
/// <item>a request with <c>Authorization: Bearer test-app:{caller}</c> authenticates as an app-only token of
/// <c>{caller}</c> (an <c>azp</c> claim, no delegated scope), which <c>EF.Auth</c> relays when the caller is in
/// <c>ForwardedClaims:TrustedCallerIds</c>;</item>
/// <item>any other request authenticates as the Scaffold principal, as in the shipped app;</item>
/// <item>the host's token credential issues <c>test-app:{TrustedCaller}</c>, so the self-call client's
/// <c>AccessTokenCache</c> token is that trusted caller's.</item>
/// </list>
/// Never referenced from <c>src</c>.
/// </summary>
internal static class SelfCallRelayTestAuth
{
    public const string TrustedCaller = "5e1fca11-0000-4000-8000-00000000f10e";
    public const string UntrustedCaller = "0bad0bad-0000-4000-8000-0000000000ba";
    public const string Scope = "api://taskflow-api-test/.default";
    private const string TokenPrefix = "test-app:";
    private const string AppTokenScheme = "TestAppToken";
    private const string SelectorScheme = "TestAppTokenOrScaffold";

    /// <summary>The host settings that turn the relay on for <see cref="TrustedCaller"/>.</summary>
    public static IReadOnlyDictionary<string, string> Environment { get; } = new Dictionary<string, string>
    {
        ["FlowEngine__SelfCall__TokenScope"] = Scope,
        ["ForwardedClaims__TrustedCallerIds__0"] = TrustedCaller,
        ["RateLimiting__Workflow__CallerIds__0"] = TrustedCaller,
        ["RateLimiting__Tenants__Budgets__workflow__PermitLimit"] = "1000000",
    };

    /// <summary>Adds the test token scheme in front of the Scaffold principal and the test credential.</summary>
    public static void Register(IServiceCollection services)
    {
        services.RemoveAll<TokenCredential>();
        services.AddSingleton<TokenCredential>(new TestAppCredential());
        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, AppTokenHandler>(AppTokenScheme, _ => { })
            .AddPolicyScheme(SelectorScheme, SelectorScheme, options => options.ForwardDefaultSelector = context =>
                context.Request.Headers.Authorization.ToString().StartsWith("Bearer ", StringComparison.Ordinal)
                    ? AppTokenScheme
                    : ScaffoldPrincipal.SchemeName);
        services.PostConfigure<AuthenticationOptions>(options => options.DefaultScheme = SelectorScheme);
    }

    /// <summary>A client that calls as <paramref name="caller"/>'s app-only token relaying <paramref name="tenantId"/>'s tenant member.</summary>
    public static void RelayAs(HttpClient client, string caller, Guid tenantId, ForwardedClaimsOptions relay)
    {
        client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", TokenPrefix + caller);
        client.DefaultRequestHeaders.Add(relay.HeaderName, ForwardedClaimsCodec.Encode(new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim("tenant_id", tenantId.ToString()),
            new Claim("sub", "relay-test-user"),
            new Claim(ClaimTypes.Role, AppConstants.ROLE_TENANT_MEMBER),
        ], "Test")), relay));
    }

    private sealed class AppTokenHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var header = Request.Headers.Authorization.ToString();
            if (!header.StartsWith("Bearer " + TokenPrefix, StringComparison.Ordinal))
                return Task.FromResult(AuthenticateResult.Fail("Not a test app token."));

            var caller = header[("Bearer " + TokenPrefix).Length..];
            var identity = new ClaimsIdentity([new Claim("azp", caller)], AppTokenScheme);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AppTokenScheme)));
        }
    }

    private sealed class TestAppCredential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            Assert.AreEqual(Scope, requestContext.Scopes.Single(), "the self-call asks for the configured scope");
            return new AccessToken(TokenPrefix + TrustedCaller, DateTimeOffset.UtcNow.AddHours(1));
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}
