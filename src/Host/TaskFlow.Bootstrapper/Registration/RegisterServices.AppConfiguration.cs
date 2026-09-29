using EF.Host;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.FeatureManagement;

namespace TaskFlow.Bootstrapper;

public static partial class RegisterServices
{
    /// <summary>App Configuration key whose change reloads every value (D-042).</summary>
    public const string AppConfigSentinelKey = "TaskFlow:Sentinel";

    /// <summary>
    /// Adds Azure App Configuration as a dynamic configuration source (D-042) through <c>EF.Host</c>: section
    /// <c>AppConfig</c> (<c>Endpoint</c>, or <c>ConnectionStrings:AppConfig</c>; <c>Label</c> layered over the null
    /// label), sentinel-key refresh (<see cref="AppConfigSentinelKey"/>), Key Vault references and feature flags, with
    /// the background refresher every host needs - no <c>UseAzureAppConfiguration</c> middleware. No-op unless an
    /// endpoint or connection string is set, so every host still boots from appsettings/env alone locally - the
    /// <c>FeatureManagement</c> section in appsettings is the fallback for feature flags in that case. The lane is
    /// resolved first, so NonAzure rejects App Configuration settings before any are used. Must run before
    /// <see cref="IHostApplicationBuilder"/> is built.
    /// </summary>
    public static IHostApplicationBuilder AddTaskFlowAppConfiguration(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        _ = HostingLaneResolver.Resolve(builder.Configuration);
        builder.AddEfAzureAppConfiguration(configure: o => o.SentinelKey = AppConfigSentinelKey);
        return builder;
    }

    /// <summary>
    /// Registers Microsoft.FeatureManagement with tenant targeting (D-042): flags read from Azure App
    /// Configuration when <see cref="AddTaskFlowAppConfiguration"/> added it, else the
    /// <c>FeatureManagement</c> section in appsettings (every flag on there). Called from the shared
    /// infrastructure registration so every host that composes through Bootstrapper - Api, Scheduler,
    /// and any RabbitMQ/Functions consumer host - resolves <see cref="IVariantFeatureManager"/> for
    /// AiTaskReviewer regardless of which one actually runs the AiReview consumer. Gateway does not
    /// reference Bootstrapper and does not evaluate any of these flags, so it is intentionally not a
    /// caller here (kept out of the "who may use Microsoft.FeatureManagement" architecture boundary).
    /// </summary>
    internal static IServiceCollection AddTaskFlowFeatureManagement(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddFeatureManagement().WithTargeting<TenantTargetingContextAccessor>();
        return services;
    }
}
