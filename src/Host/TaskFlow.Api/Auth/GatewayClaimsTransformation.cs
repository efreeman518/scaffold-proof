using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TaskFlow.Api.Auth;

/// <summary>Configures gateway claims transform host behavior for TaskFlow runtime services.</summary>
public sealed class GatewayClaimsTransformSettings
{
    public const string ConfigSectionName = "GatewayClaimsTransform";

    /// <summary>Claim type stamped on a relayed principal, carrying the trusted gateway id that relayed it.</summary>
    public const string RelayedByClaimType = "relayed_by";

    public string HeaderName { get; set; } = "X-Orig-Request";

    /// <summary>
    /// The gateway's client id, matched against the caller token's <c>azp</c>/<c>appid</c>. Empty disables the
    /// relay (fail closed): there is deliberately no "trust any caller" switch.
    /// </summary>
    public string GatewayAppId { get; set; } = "";
}

/// <summary>
/// Rehydrates original user claims forwarded by the trusted gateway after the API validates the service token.
/// <para>
/// Trust boundary: the header is honored only for an authenticated app-only token issued to the gateway
/// (<c>azp</c>/<c>appid</c> equals <see cref="GatewayClaimsTransformSettings.GatewayAppId"/> and no delegated
/// <c>scp</c> claim - a user token issued to the gateway's client id also carries that <c>azp</c>, and would
/// otherwise let its holder call the API directly and forge any identity). On success the result is a NEW
/// principal holding only the relayed user claims plus <see cref="GatewayClaimsTransformSettings.RelayedByClaimType"/>:
/// none of the gateway service identity's claims (oid, roles, azp) are carried over, so the request is
/// attributed to the user and never inherits the gateway's app roles. A second run is a no-op, because the
/// relayed principal carries no trusted-caller claim.
/// </para>
/// </summary>
public sealed class GatewayClaimsTransformer(
    ILogger<GatewayClaimsTransformer> logger,
    IHttpContextAccessor httpContextAccessor,
    IOptions<GatewayClaimsTransformSettings> options) : IClaimsTransformation
{
    private readonly GatewayClaimsTransformSettings _settings = options.Value;

    /// <summary>Provides the transform operation for gateway claims transformer.</summary>
    public Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        if (principal.Identity is not ClaimsIdentity identity || !identity.IsAuthenticated)
            return Task.FromResult(principal);

        if (!IsTrustedGatewayAppToken(principal))
            return Task.FromResult(principal);

        var httpContext = httpContextAccessor.HttpContext;
        if (httpContext is null || !httpContext.Request.Headers.TryGetValue(_settings.HeaderName, out var values))
            return Task.FromResult(principal);

        var header = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(header))
            return Task.FromResult(principal);

        ForwardedClaims? forwardedClaims = null;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(header));
            forwardedClaims = JsonSerializer.Deserialize<ForwardedClaims>(json);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            logger.GatewayClaimsParseFailed(ex, _settings.HeaderName);
        }

        if (forwardedClaims is null)
            return Task.FromResult(principal);

        // A new identity, not a clone of the gateway's: only the relayed user claims go in.
        var relayed = new ClaimsIdentity(identity.AuthenticationType, ClaimTypes.Name, ClaimTypes.Role);

        AddClaimIfMissing(relayed, "oid", forwardedClaims.Sub);
        AddClaimIfMissing(relayed, ClaimTypes.NameIdentifier, forwardedClaims.Sub);
        AddClaimIfMissing(relayed, "tenant_id", forwardedClaims.TenantId);
        AddClaimIfMissing(relayed, ClaimTypes.Name, forwardedClaims.Name);

        if (forwardedClaims.Roles is { Length: > 0 })
        {
            foreach (var role in forwardedClaims.Roles)
            {
                AddClaimIfMissing(relayed, ClaimTypes.Role, role);
            }
        }

        relayed.AddClaim(new Claim(GatewayClaimsTransformSettings.RelayedByClaimType, _settings.GatewayAppId));
        return Task.FromResult(new ClaimsPrincipal(relayed));
    }

    /// <summary>
    /// True only for an app-only token issued to the configured gateway: a trusted <c>azp</c>/<c>appid</c> and no
    /// delegated-scope claim. An empty <see cref="GatewayClaimsTransformSettings.GatewayAppId"/> trusts nobody.
    /// </summary>
    private bool IsTrustedGatewayAppToken(ClaimsPrincipal principal)
    {
        if (string.IsNullOrWhiteSpace(_settings.GatewayAppId))
            return false;

        var isGateway = principal.HasClaim(c =>
            (string.Equals(c.Type, "azp", StringComparison.OrdinalIgnoreCase)
             || string.Equals(c.Type, "appid", StringComparison.OrdinalIgnoreCase))
            && string.Equals(c.Value, _settings.GatewayAppId, StringComparison.OrdinalIgnoreCase));

        return isGateway && !principal.HasClaim(c => DelegatedScopeClaimTypes.Contains(c.Type));
    }

    /// <summary>Claim types that mark a delegated (user) token; an app-only client-credentials token has neither.</summary>
    private static readonly HashSet<string> DelegatedScopeClaimTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "scp",
        "http://schemas.microsoft.com/identity/claims/scope"
    };

    /// <summary>Registers claim if missing dependencies in the service container.</summary>
    private static void AddClaimIfMissing(ClaimsIdentity identity, string type, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        if (!identity.HasClaim(c => c.Type == type && c.Value == value))
            identity.AddClaim(new Claim(type, value));
    }

    /// <summary>Configures forwarded claims host behavior for TaskFlow runtime services.</summary>
    private sealed record ForwardedClaims(
        [property: JsonPropertyName("sub")] string? Sub,
        [property: JsonPropertyName("tenant_id")] string? TenantId,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("roles")] string[]? Roles);
}
