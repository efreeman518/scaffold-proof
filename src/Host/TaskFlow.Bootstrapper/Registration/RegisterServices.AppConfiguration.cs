using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.FeatureManagement;

namespace TaskFlow.Bootstrapper;

public static partial class RegisterServices
{
    public const string AppConfigEndpointConfigKey = "AppConfig:Endpoint";
    public const string AppConfigLabelConfigKey = "AppConfig:Label";

    /// <summary>Interval both the sentinel-key refresh and the feature-flag refresh poll at (D-042).</summary>
    private static readonly TimeSpan AppConfigRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Adds Azure App Configuration as a dynamic configuration source (D-042): sentinel-key refresh
    /// (<c>TaskFlow:Sentinel</c>), Key Vault references for secrets, and feature flags. No-op unless
    /// <c>AppConfig:Endpoint</c> or <c>ConnectionStrings:AppConfig</c> is set, so every host still boots
    /// from appsettings/env alone locally - the <c>FeatureManagement</c> section in appsettings is the
    /// fallback for feature flags in that case. Must run before <see cref="IHostApplicationBuilder.Build"/>
    /// so the added provider is part of the built configuration.
    /// </summary>
    public static IHostApplicationBuilder AddTaskFlowAppConfiguration(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var config = builder.Configuration;

        var endpoint = config[AppConfigEndpointConfigKey];
        var connectionString = config.GetConnectionString("AppConfig");
        if (string.IsNullOrWhiteSpace(endpoint) && string.IsNullOrWhiteSpace(connectionString))
            return builder;

        var label = config[AppConfigLabelConfigKey];
        var labelFilter = string.IsNullOrWhiteSpace(label) ? LabelFilter.Null : label;
        var credential = CreateAzureCredential(config);

        config.AddAzureAppConfiguration(o =>
        {
            if (!string.IsNullOrWhiteSpace(endpoint))
                o.Connect(new Uri(endpoint), credential);
            else
                o.Connect(connectionString);

            o.ConfigureKeyVault(kv => kv.SetCredential(credential))
                .Select(KeyFilter.Any, labelFilter)
                .ConfigureRefresh(r => r
                    .Register("TaskFlow:Sentinel", refreshAll: true)
                    .SetRefreshInterval(AppConfigRefreshInterval))
                .UseFeatureFlags(ff => ff.SetRefreshInterval(AppConfigRefreshInterval));
        });

        builder.Services.AddAzureAppConfiguration();
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
