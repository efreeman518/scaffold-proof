using Microsoft.FeatureManagement;
using Microsoft.FeatureManagement.AspNetCore;

namespace TaskFlow.Api.Filters;

/// <summary>
/// Gates the requests <c>appliesWhen</c> selects behind a dynamic feature flag (D-042). Answers 404, not 403: a
/// disabled surface should look absent, not merely forbidden, so its existence does not leak to a caller probing
/// the API. A whole-route gate needs no app code - <see cref="FeatureGateEndpointExtensions.RequireFeature{TBuilder}(TBuilder, string)"/>
/// uses Microsoft.FeatureManagement's own <c>WithFeatureGate</c>, which has the same 404 contract; only this
/// per-request narrowing has no Microsoft equivalent.
/// Constructed directly (not DI-activated) so the flag name can be supplied per route; the feature
/// manager itself is still resolved per-request from <see cref="HttpContext.RequestServices"/>.
/// </summary>
/// <param name="featureName">Flag that must be on for the route to answer.</param>
/// <param name="appliesWhen">
/// Optional narrowing predicate over the bound arguments. Some flags gate one shape of a request rather than
/// a whole route - SemanticSearch gates only <c>mode=Semantic</c> on /search/tasks (AR-04), and keyword search
/// has to keep answering while it is off. Null means the flag gates every request to the route.
/// </param>
internal sealed class FeatureGateEndpointFilter(
    string featureName,
    Func<EndpointFilterInvocationContext, bool>? appliesWhen = null) : IEndpointFilter
{
    /// <summary>Short-circuits with 404 when <paramref name="featureName"/> is off.</summary>
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (appliesWhen is not null && !appliesWhen(context)) return await next(context);

        var httpContext = context.HttpContext;
        var featureManager = httpContext.RequestServices.GetRequiredService<IVariantFeatureManager>();

        var enabled = await featureManager.IsEnabledAsync(featureName, httpContext.RequestAborted);
        return enabled ? await next(context) : Results.NotFound();
    }
}

/// <summary>
/// Route-builder helpers for the D-042 feature-flag contract. They live here so Microsoft.FeatureManagement stays
/// inside TaskFlow.Api.Filters (the architecture boundary D-042 sets for the Api host).
/// </summary>
public static class FeatureGateEndpointExtensions
{
    /// <summary>The route answers 404 while <paramref name="featureName"/> is off (Microsoft's <c>WithFeatureGate</c>).</summary>
    public static TBuilder RequireFeature<TBuilder>(this TBuilder builder, string featureName)
        where TBuilder : IEndpointConventionBuilder
    {
        builder.WithFeatureGate(featureName);
        return builder;
    }

    /// <summary>
    /// Same gate, applied only to the requests <paramref name="appliesWhen"/> selects. For a flag that gates
    /// one mode of a route rather than the route itself.
    /// </summary>
    public static TBuilder RequireFeature<TBuilder>(
        this TBuilder builder, string featureName, Func<EndpointFilterInvocationContext, bool> appliesWhen)
        where TBuilder : IEndpointConventionBuilder =>
        builder.AddEndpointFilter(new FeatureGateEndpointFilter(featureName, appliesWhen));
}
