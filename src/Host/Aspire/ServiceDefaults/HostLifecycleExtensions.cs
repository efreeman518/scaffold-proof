using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Microsoft.Extensions.Hosting;

/// <summary>Registers the D-064 shutdown budget, drain step, and runtime-profile log.</summary>
public static class HostLifecycleExtensions
{
    /// <summary>
    /// Binds <c>Hosting:ShutdownTimeoutSeconds</c> (default 25, below the 30 s Container Apps termination grace)
    /// and <c>Hosting:DrainDelaySeconds</c> (default 0; deployments set it). The drain must fit inside the
    /// shutdown budget, otherwise the budget would cancel it and cut in-flight requests anyway.
    /// </summary>
    public static IHostApplicationBuilder AddHostLifecycle(this IHostApplicationBuilder builder)
    {
        var shutdownTimeout = TimeSpan.FromSeconds(builder.Configuration.GetValue("Hosting:ShutdownTimeoutSeconds", 25));
        var drainDelay = TimeSpan.FromSeconds(builder.Configuration.GetValue("Hosting:DrainDelaySeconds", 0));
        if (drainDelay < TimeSpan.Zero || drainDelay >= shutdownTimeout)
        {
            throw new InvalidOperationException(
                $"Hosting:DrainDelaySeconds ({drainDelay.TotalSeconds}) must be at least 0 and below " +
                $"Hosting:ShutdownTimeoutSeconds ({shutdownTimeout.TotalSeconds}).");
        }

        builder.Services.Configure<HostOptions>(options => options.ShutdownTimeout = shutdownTimeout);
        builder.Services.AddSingleton(sp => new HostLifecycleService(
            drainDelay, sp.GetRequiredService<ILogger<HostLifecycleService>>()));
        builder.Services.AddHostedService(sp => sp.GetRequiredService<HostLifecycleService>());
        builder.Services.AddHealthChecks().AddCheck<HostLifecycleService>(HostLifecycleService.HealthCheckName, tags: ["ready"]);
        return builder;
    }
}
