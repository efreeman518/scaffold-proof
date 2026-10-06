using System.Security.Claims;
using EF.Auth.Relay;
using Microsoft.AspNetCore.Authentication;

namespace TaskFlow.Api.Auth;

/// <summary>
/// Runs the <c>EF.Auth</c> relay transformation (D-068) and records, for the request, the principal it built from a
/// trusted caller's relay header. A relayed-by claim alone does not prove that: the package returns any principal it
/// does not relay unchanged, so a token whose issuer put a claim of that name on it keeps it. Only a principal this
/// transformation replaced is <see cref="IsRelayed"/>.
/// </summary>
internal sealed class RelayedPrincipalMarker(ForwardedClaimsTransformation relay, IHttpContextAccessor accessor) : IClaimsTransformation
{
    private static readonly object Key = new();

    public async Task<ClaimsPrincipal> TransformAsync(ClaimsPrincipal principal)
    {
        var result = await relay.TransformAsync(principal).ConfigureAwait(false);
        if (!ReferenceEquals(result, principal) && result.Identity?.IsAuthenticated == true && accessor.HttpContext is { } context)
            context.Items[Key] = result;
        return result;
    }

    /// <summary>Whether the request's user is the principal the relay built from a trusted caller's header.</summary>
    public static bool IsRelayed(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.Items.TryGetValue(Key, out var relayed) && ReferenceEquals(relayed, context.User);
    }
}
