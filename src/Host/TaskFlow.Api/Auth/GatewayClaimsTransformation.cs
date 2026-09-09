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

    public string HeaderName { get; set; } = "X-Orig-Request";
    public string GatewayAppId { get; set; } = "";
    public bool RequireTrustedGateway { get; set; } = true;
}

/// <summary>
/// Rehydrates original user claims forwarded by the trusted gateway after the API validates the service token.
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

        if (!IsTrustedGatewayCaller(principal))
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

        var newIdentity = identity.Clone();

        AddClaimIfMissing(newIdentity, "oid", forwardedClaims.Sub);
        AddClaimIfMissing(newIdentity, ClaimTypes.NameIdentifier, forwardedClaims.Sub);
        AddClaimIfMissing(newIdentity, "tenant_id", forwardedClaims.TenantId);
        AddClaimIfMissing(newIdentity, ClaimTypes.Name, forwardedClaims.Name);

        if (forwardedClaims.Roles is { Length: > 0 })
        {
            foreach (var role in forwardedClaims.Roles)
            {
                AddClaimIfMissing(newIdentity, ClaimTypes.Role, role);
            }
        }

        return Task.FromResult(new ClaimsPrincipal(newIdentity));
    }

    /// <summary>Provides the is trusted gateway caller operation for gateway claims transformer.</summary>
    private bool IsTrustedGatewayCaller(ClaimsPrincipal principal)
    {
        if (!_settings.RequireTrustedGateway)
            return true;

        if (string.IsNullOrWhiteSpace(_settings.GatewayAppId))
            return false;

        return principal.HasClaim(c =>
            (string.Equals(c.Type, "azp", StringComparison.OrdinalIgnoreCase)
             || string.Equals(c.Type, "appid", StringComparison.OrdinalIgnoreCase))
            && string.Equals(c.Value, _settings.GatewayAppId, StringComparison.OrdinalIgnoreCase));
    }

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
