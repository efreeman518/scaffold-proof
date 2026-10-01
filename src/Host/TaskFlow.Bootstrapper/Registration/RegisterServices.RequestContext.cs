using EF.AspNetCore.RequestContext;
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
    /// get it, and a real <see cref="AppConstants.ROLE_GLOBAL_ADMIN"/> claim is kept.</item>
    /// <item>An unauthenticated HTTP request: anonymous - no tenant, no roles. Nothing is invented for it; the
    /// scaffold auth handler authenticates every Api request, so only anonymous surfaces (Functions HTTP triggers)
    /// see this.</item>
    /// <item>No HTTP request (message consumers, scheduled jobs, the AI reviewer, Functions queue triggers):
    /// the package system context - no tenant, <see cref="AppConstants.SYSTEM_USER_ID"/>, and the one role
    /// <see cref="AppConstants.ROLE_SYSTEM"/>. It acts for the tenant the data names; EF.Tenancy lists that role in
    /// <c>CrossTenantRoles</c> (AddSharedApplicationServices), so it passes the tenant boundary (D-067). It must not
    /// borrow the scaffold admin identity: that would pin background reads to the scaffold tenant through the tenant
    /// query filter, target tenant flags at it, and attribute background writes to the scaffold user.</item>
    /// </list>
    /// </summary>
    internal static void AddRequestContext(IServiceCollection services) =>
        services.AddHttpRequestContext<Guid?>(
            value => Guid.TryParse(value, out var tenantId) ? tenantId : null,
            options =>
            {
                options.SystemAuditId = AppConstants.SYSTEM_USER_ID;
                options.SystemRoles = [AppConstants.ROLE_SYSTEM];
            });
}
