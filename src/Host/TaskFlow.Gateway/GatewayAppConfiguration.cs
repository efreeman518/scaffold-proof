using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Configuration.AzureAppConfiguration;

namespace TaskFlow.Gateway;

/// <summary>
/// Gateway's own Azure App Configuration wiring (D-042). Gateway does not reference TaskFlow.Bootstrapper
/// (it stays a thin edge project - see TaskFlow.Gateway.csproj), so it cannot share
/// RegisterServices.AddTaskFlowAppConfiguration; this is the same handful of calls against a plain
/// DefaultAzureCredential instead of Bootstrapper's ManagedIdentityClientId/SharedTokenCacheTenantId-aware
/// CreateAzureCredential. Deliberately does not call AddFeatureManagement: the Gateway evaluates none of
/// the D-042 flags, so it stays out of the "who may use Microsoft.FeatureManagement" architecture rule.
/// </summary>
internal static class GatewayAppConfiguration
{
    private const string EndpointConfigKey = "AppConfig:Endpoint";
    private const string LabelConfigKey = "AppConfig:Label";
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>No-op unless AppConfig:Endpoint or ConnectionStrings:AppConfig is set.</summary>
    internal static IHostApplicationBuilder AddGatewayAppConfiguration(this IHostApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var endpoint = config[EndpointConfigKey];
        var connectionString = config.GetConnectionString("AppConfig");
        if (string.IsNullOrWhiteSpace(endpoint) && string.IsNullOrWhiteSpace(connectionString))
            return builder;

        var label = config[LabelConfigKey];
        var labelFilter = string.IsNullOrWhiteSpace(label) ? LabelFilter.Null : label;
        var credential = new DefaultAzureCredential();

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
                    .SetRefreshInterval(RefreshInterval));
        });

        builder.Services.AddAzureAppConfiguration();
        return builder;
    }

    /// <summary>True when AddGatewayAppConfiguration actually added the provider - guards the middleware.</summary>
    internal static bool IsAppConfigurationConfigured(IConfiguration config) =>
        !string.IsNullOrWhiteSpace(config[EndpointConfigKey]) || !string.IsNullOrWhiteSpace(config.GetConnectionString("AppConfig"));
}
