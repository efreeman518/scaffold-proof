using System.Net.Http.Headers;
using System.Security.Claims;
using EF.AspNetCore.RequestContext;
using EF.Auth.Relay;
using EF.Auth.Tokens;
using EF.FlowEngine;
using Microsoft.Extensions.Options;
using TaskFlow.Application.Contracts;

namespace TaskFlow.Bootstrapper;

/// <summary>
/// The workflow self-call relay on the FlowEngine <c>taskflow-api</c> client (D-068, D-075). The header name and the
/// claim allowlist come from the shared <c>ForwardedClaims</c> section, so the header is the one the Api reads.
/// <list type="bullet">
/// <item>Every request: any relay header already on it or on its content (a node's <c>headers</c> config can name any
/// header) is removed, so the only relay header this client sends is the one written here.</item>
/// <item>Relay configured (<see cref="SelfCallRelayOptions.TokenScope"/>): the request must go to the self-call base
/// address (<c>FlowEngine:TaskFlowApiBaseUrl</c>: same scheme, host and port), because a node's path or a fetch URL can
/// be absolute or scheme-relative and the token must never leave for another host; a request carrying its own
/// <c>Authorization</c> header is refused too, so a node cannot replace the token. Then an app-only bearer token for that scope from
/// <c>EF.Auth</c> <see cref="AccessTokenCache"/>, and a relay header carrying the executing instance's tenant
/// (<see cref="FlowExecution.Current"/>), <see cref="Subject"/>, <see cref="DisplayName"/> and
/// <see cref="Roles"/>. A call outside an instance with a tenant throws before anything is sent: it never falls back to
/// the host's own identity. The integration node takes its Error edge.</item>
/// <item>Relay not configured: no token and no header; the call runs as the Api's own authentication.</item>
/// </list>
/// </summary>
internal sealed class SelfCallRelayHandler(
    AccessTokenCache tokens,
    IOptions<SelfCallRelayOptions> relay,
    IOptionsMonitor<ForwardedClaimsOptions> forwardedClaims,
    IOptions<HttpRequestContextOptions> requestContext,
    TimeProvider timeProvider) : DelegatingHandler
{
    /// <summary>The relayed subject: the Api's audit id for every workflow self-call write.</summary>
    public const string Subject = "taskflow-workflow";

    public const string DisplayName = "TaskFlow workflow";

    /// <summary>
    /// The relayed roles: <see cref="AppConstants.ROLE_TENANT_MEMBER"/> alone. The self-called endpoints (task search
    /// and create, the task patch, comment posts, attachment search) require an authenticated caller, and the tenant
    /// boundary refuses a caller with no role, so one tenant role is the minimum. Never a cross-tenant role
    /// (<see cref="AppConstants.ROLE_GLOBAL_ADMIN"/>, <see cref="AppConstants.ROLE_SYSTEM"/>).
    /// </summary>
    public static IReadOnlyList<string> Roles { get; } = [AppConstants.ROLE_TENANT_MEMBER];

    private const string SubjectClaimType = "sub";

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var claimsOptions = forwardedClaims.Get(SelfCallRelayOptions.ForwardedClaimsOptionsName);
        request.Headers.Remove(claimsOptions.HeaderName);
        request.Content?.Headers.Remove(claimsOptions.HeaderName);

        var settings = relay.Value;
        if (!settings.IsRelayConfigured)
            return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);

        // FlowEngine holds the requests its nodes build to this client's BaseAddress (BaseAddressBoundary); this check
        // guards the credential this handler attaches for any sender on the named client, whoever built the request.
        if (!IsSelfCallTarget(request.RequestUri, settings.ApiBaseAddress))
        {
            throw new InvalidOperationException(
                $"The workflow self-call to {request.RequestUri} leaves the self-call base address {settings.ApiBaseAddress}; " +
                "the relayed token is sent to that address only.");
        }

        if (request.Headers.Authorization is not null)
        {
            throw new InvalidOperationException(
                $"The workflow self-call to {request.RequestUri} carries its own Authorization header; with " +
                $"{SelfCallRelayOptions.ConfigSectionName}:TokenScope set the relay supplies the token.");
        }

        var tenant = FlowExecution.Current?.TenantId;
        if (!Guid.TryParse(tenant, out var tenantId))
        {
            throw new InvalidOperationException(
                $"The workflow self-call to {request.RequestUri} runs outside an instance with a tenant (tenant '{tenant}'); " +
                $"with {SelfCallRelayOptions.ConfigSectionName}:TokenScope set it acts only for the instance tenant.");
        }

        var token = await tokens.GetTokenAsync([settings.TokenScope!], cancellationToken).ConfigureAwait(false);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        request.Headers.Add(
            claimsOptions.HeaderName,
            ForwardedClaimsCodec.Encode(RelayedPrincipal(tenantId, requestContext.Value), claimsOptions, timeProvider));
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Whether <paramref name="target"/> has the scheme, host and port of <paramref name="baseAddress"/> (host compared
    /// ignoring case, a default port equal to its explicit form).
    /// </summary>
    internal static bool IsSelfCallTarget(Uri? target, Uri? baseAddress) =>
        target is { IsAbsoluteUri: true } && baseAddress is { IsAbsoluteUri: true }
        && Uri.Compare(target, baseAddress, UriComponents.Scheme | UriComponents.Host | UriComponents.StrongPort,
            UriFormat.SafeUnescaped, StringComparison.OrdinalIgnoreCase) == 0;

    /// <summary>
    /// The relayed identity for <paramref name="tenantId"/>, in the claim types the Api's request context reads
    /// (<see cref="HttpRequestContextOptions.TenantClaimType"/>, <see cref="HttpRequestContextOptions.RoleClaimType"/>).
    /// </summary>
    internal static ClaimsPrincipal RelayedPrincipal(Guid tenantId, HttpRequestContextOptions requestContext) =>
        new(new ClaimsIdentity(
        [
            new Claim(requestContext.TenantClaimType, tenantId.ToString()),
            new Claim(SubjectClaimType, Subject),
            new Claim(ClaimTypes.Name, DisplayName),
            .. Roles.Select(role => new Claim(requestContext.RoleClaimType, role)),
        ], SelfCallRelayOptions.ForwardedClaimsOptionsName));

    /// <summary>
    /// The relayed claim types the allowlist in <paramref name="claimsOptions"/> drops, found by encoding and decoding a
    /// relay header as the Api does. Empty when the Api would receive every one of them.
    /// </summary>
    internal static IReadOnlyList<string> DroppedClaimTypes(
        ForwardedClaimsOptions claimsOptions, HttpRequestContextOptions requestContext)
    {
        var principal = RelayedPrincipal(SelfCallRelayOptions.ScaffoldTenantId, requestContext);
        var sent = principal.Claims.Select(c => c.Type).Distinct(StringComparer.Ordinal).ToList();
        var header = ForwardedClaimsCodec.Encode(principal, claimsOptions);
        if (!ForwardedClaimsCodec.TryDecode(header, claimsOptions, out var received))
            return sent;
        return [.. sent.Where(type => !received.Any(c => c.Type == type))];
    }
}
