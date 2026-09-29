using System.Diagnostics;
using EF.AspNetCore.RequestContext;
using EF.Common.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Contracts;

namespace TaskFlow.Bootstrapper;

/// <summary>Configures register services host behavior for TaskFlow runtime services.</summary>
public static partial class RegisterServices
{
    /// <summary>
    /// Registers the scoped claims-based request context (<c>EF.AspNetCore</c>). Three cases, decided by whether an
    /// HTTP request exists:
    /// <list type="bullet">
    /// <item>An authenticated HTTP caller: identity (<c>oid</c>, then name identifier, then <c>sub</c>), tenant
    /// (<c>tenant_id</c>) and roles from its claims; a token claiming <see cref="AppConstants.ROLE_SYSTEM"/> does not
    /// get it.</item>
    /// <item>An unauthenticated HTTP request: anonymous - no tenant, no roles. Nothing is invented for it; the
    /// scaffold auth handler authenticates every Api request, so only anonymous surfaces (Functions HTTP triggers)
    /// see this.</item>
    /// <item>No HTTP request (message consumers, scheduled jobs, the AI reviewer, Functions queue triggers):
    /// the explicit system identity - no tenant, <see cref="AppConstants.SYSTEM_USER_ID"/>, and the roles
    /// <see cref="AppConstants.ROLE_SYSTEM"/> plus <see cref="AppConstants.ROLE_GLOBAL_ADMIN"/>. It acts for the tenant
    /// the data names, and EF.Tenancy lets only the global-admin role past the tenant boundary (D14), so it carries
    /// that role. It must not borrow the scaffold admin identity: that would pin background reads to the scaffold
    /// tenant through the tenant query filter, target tenant flags at it, and attribute background writes to the
    /// scaffold user.</item>
    /// </list>
    /// </summary>
    internal static void AddRequestContext(IServiceCollection services)
    {
        services.AddHttpRequestContext<Guid?>(
            value => Guid.TryParse(value, out var tenantId) ? tenantId : null,
            options =>
            {
                options.SystemAuditId = AppConstants.SYSTEM_USER_ID;
                options.SystemRole = AppConstants.ROLE_SYSTEM;
            });

        // The package gives the no-request context exactly one role (SystemRole); EF.Tenancy needs GlobalAdmin
        // there as well, so the no-request branch is TaskFlow's own. HTTP requests keep the claims-based context,
        // which strips only the system role from a token - a real GlobalAdmin claim is kept.
        var claimsContext = services.Last(d => d.ServiceType == typeof(IRequestContext<string, Guid?>)).ImplementationFactory!;
        services.AddScoped<IRequestContext<string, Guid?>>(sp =>
            sp.GetRequiredService<IHttpContextAccessor>().HttpContext is null
                ? new RequestContext<string, Guid?>(
                    Activity.Current is { IdFormat: ActivityIdFormat.W3C } activity
                        ? activity.TraceId.ToHexString()
                        : Guid.NewGuid().ToString("N"),
                    AppConstants.SYSTEM_USER_ID,
                    null,
                    [AppConstants.ROLE_SYSTEM, AppConstants.ROLE_GLOBAL_ADMIN])
                : (IRequestContext<string, Guid?>)claimsContext(sp));
    }
}
