using EF.Common.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement.FeatureFilters;

namespace TaskFlow.Bootstrapper;

/// <summary>
/// Feeds Microsoft.FeatureManagement's targeting filter from the app's existing request-context
/// abstraction (D-042): the tenant id becomes the targeting <see cref="TargetingContext.UserId"/>.
/// <para>
/// <c>WithTargeting</c> registers this accessor as a singleton next to the singleton feature manager, so the
/// provider it is built with is the root provider. The scoped <see cref="IRequestContext{TId,TTenantId}"/> is
/// therefore resolved per call: from the current request's services during an HTTP request, and from a fresh
/// scope otherwise (message consumers, scheduled jobs), which yields the system identity. Resolving it from the
/// root provider instead would cache the first caller's context for the process and target every later tenant
/// as that one.
/// </para>
/// Resolved via <see cref="IServiceProvider.GetService"/> rather than a constructor dependency because
/// not every host that registers feature management also registers <see cref="IRequestContext{TId,TTenantId}"/>
/// (Gateway does not) - an unresolved request context (or the system context with no tenant) falls through to an
/// empty targeting id, which is a safe "no tenant-specific targeting" default rather than a startup failure.
/// </summary>
internal sealed class TenantTargetingContextAccessor(IServiceProvider serviceProvider) : ITargetingContextAccessor
{
    /// <inheritdoc />
    public ValueTask<TargetingContext> GetContextAsync()
    {
        var requestServices = serviceProvider.GetService<IHttpContextAccessor>()?.HttpContext?.RequestServices;
        if (requestServices is not null)
            return ValueTask.FromResult(TargetingFor(requestServices));

        using var scope = serviceProvider.CreateScope();
        return ValueTask.FromResult(TargetingFor(scope.ServiceProvider));
    }

    private static TargetingContext TargetingFor(IServiceProvider services)
    {
        var tenantId = services.GetService<IRequestContext<string, Guid?>>()?.TenantId?.ToString() ?? string.Empty;
        return new TargetingContext { UserId = tenantId };
    }
}
