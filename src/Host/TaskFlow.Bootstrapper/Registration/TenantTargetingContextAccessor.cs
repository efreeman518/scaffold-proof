using EF.Common.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FeatureManagement.FeatureFilters;

namespace TaskFlow.Bootstrapper;

/// <summary>
/// Feeds Microsoft.FeatureManagement's targeting filter from the app's existing request-context
/// abstraction (D-042): the tenant id becomes the targeting <see cref="TargetingContext.UserId"/>.
/// Resolved via <see cref="IServiceProvider.GetService"/> rather than a constructor dependency because
/// not every host that registers feature management also registers <see cref="IRequestContext{TId,TTenantId}"/>
/// (Gateway does not) - an unresolved request context (or the ambient default the Scheduler/background
/// consumers get when there is no HTTP request) both fall through to an empty targeting id, which is a
/// safe "no tenant-specific targeting" default rather than a startup failure.
/// </summary>
internal sealed class TenantTargetingContextAccessor(IServiceProvider serviceProvider) : ITargetingContextAccessor
{
    /// <inheritdoc />
    public ValueTask<TargetingContext> GetContextAsync()
    {
        var requestContext = serviceProvider.GetService<IRequestContext<string, Guid?>>();
        var tenantId = requestContext?.TenantId?.ToString() ?? string.Empty;
        return ValueTask.FromResult(new TargetingContext { UserId = tenantId });
    }
}
