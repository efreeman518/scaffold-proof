using EF.Common.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Security.Claims;
using TaskFlow.Application.Contracts;

namespace TaskFlow.Bootstrapper;

/// <summary>Configures register services host behavior for TaskFlow runtime services.</summary>
public static partial class RegisterServices
{
    /// <summary>
    /// Registers the scoped request context. Three cases, decided by whether an HTTP request exists:
    /// <list type="bullet">
    /// <item>An authenticated HTTP caller: identity, tenant and roles from its claims.</item>
    /// <item>An HTTP request with no authenticated user in scaffold auth mode: the scaffold fixed identity,
    /// the same one <c>ScaffoldAuthHandler</c> issues.</item>
    /// <item>No HTTP request (message consumers, scheduled jobs, the AI reviewer, Functions queue triggers):
    /// the explicit system identity - no tenant, <see cref="AppConstants.SYSTEM_USER_ID"/>, and only
    /// <see cref="AppConstants.ROLE_SYSTEM"/>. It must not borrow the scaffold admin: that would pin background
    /// reads to the scaffold tenant through the tenant query filter, target tenant flags at it, and attribute
    /// background writes to a global admin.</item>
    /// </list>
    /// </summary>
    internal static void AddRequestContext(IServiceCollection services)
    {
        services.AddHttpContextAccessor();

        services.AddScoped<IRequestContext<string, Guid?>>(sp =>
        {
            var httpContext = sp.GetRequiredService<IHttpContextAccessor>().HttpContext;
            if (httpContext is null)
            {
                return new RequestContext<string, Guid?>(
                    Guid.NewGuid().ToString(),
                    AppConstants.SYSTEM_USER_ID,
                    null,
                    [AppConstants.ROLE_SYSTEM]);
            }

            var correlationId = httpContext.Request.Headers["X-Correlation-Id"].FirstOrDefault()
                             ?? Guid.NewGuid().ToString();
            var user = httpContext.User;

            if (user.Identity?.IsAuthenticated != true)
            {
                // Scaffold is the only AuthMode (AuthModeResolver rejects anything else); a real identity
                // provider mode must answer an unauthenticated request with an anonymous context here instead.
                _ = AuthModeResolver.Resolve(sp.GetRequiredService<IConfiguration>()[AuthModeResolver.ConfigKey]);
                return new RequestContext<string, Guid?>(
                    correlationId,
                    "scaffold-user",
                    Guid.Parse("00000000-0000-0000-0000-000000000001"),
                    new List<string>
                    {
                        AppConstants.ROLE_GLOBAL_ADMIN,
                        AppConstants.ROLE_TENANT_ADMIN,
                        AppConstants.ROLE_TENANT_MEMBER,
                    });
            }

            var userId = user.FindFirst("oid")?.Value
                      ?? user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                      ?? user.FindFirst("sub")?.Value
                      ?? "unknown";

            var tenantClaim = user.FindFirst("tenant_id")?.Value;
            Guid? tenantId = Guid.TryParse(tenantClaim, out var tid) ? tid : null;

            // The system role is minted here for no-request work only; a token claiming it gains nothing.
            var roles = user.FindAll(ClaimTypes.Role)
                           .Select(c => c.Value)
                           .Where(role => !string.Equals(role, AppConstants.ROLE_SYSTEM, StringComparison.OrdinalIgnoreCase))
                           .ToList();

            return new RequestContext<string, Guid?>(correlationId, userId, tenantId, roles);
        });
    }
}
