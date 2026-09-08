using Microsoft.FeatureManagement;

namespace TaskFlow.Api.Filters;

/// <summary>
/// Gates a route behind a dynamic feature flag (D-042). Answers 404, not 403: a disabled surface should
/// look absent, not merely forbidden, so its existence does not leak to a caller probing the API.
/// Constructed directly (not DI-activated) so the flag name can be supplied per route; the feature
/// manager itself is still resolved per-request from <see cref="HttpContext.RequestServices"/>.
/// </summary>
internal sealed class FeatureGateEndpointFilter(string featureName) : IEndpointFilter
{
    /// <summary>Short-circuits with 404 when <paramref name="featureName"/> is off.</summary>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        var featureManager = httpContext.RequestServices.GetRequiredService<IVariantFeatureManager>();

        var enabled = await featureManager.IsEnabledAsync(featureName, httpContext.RequestAborted);
        return enabled ? await next(context) : Results.NotFound();
    }
}

/// <summary>Route-builder helper for the D-042 feature-flag contract.</summary>
public static class FeatureGateEndpointExtensions
{
    /// <summary>Adds the feature-gate filter so the route answers 404 while <paramref name="featureName"/> is off.</summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, string featureName)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new FeatureGateEndpointFilter(featureName));
}
