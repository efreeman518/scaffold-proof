using EF.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using OpenTelemetry.Metrics;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Observability.Meters;
using TaskFlow.Infrastructure.Caching.RateLimiting;
using ZiggyCreatures.Caching.Fusion;

namespace TaskFlow.Infrastructure.Caching;

/// <summary>
/// Composition for the cache tier. Lives beside the telemetry wrapper rather than in the Bootstrapper so the
/// EF.Cache and FusionCache packages stay behind this project's boundary.
/// </summary>
public static class RegisterCachingServices
{
    /// <summary>Configuration section holding the <see cref="CacheSettings"/> array.</summary>
    public const string SectionName = "CacheSettings";

    /// <summary>
    /// Bumped whenever a cached snapshot's shape changes. It is part of every key, so a deployment with a new
    /// shape simply cannot read the old one - no eviction sweep, no version-mismatch deserialization failures.
    /// <para>
    /// 2: the JSON cache serializer no longer writes <c>ReferenceHandler.Preserve</c> metadata, so entries
    /// written by a build older than that carry an <c>$id</c>/<c>$values</c> wrapper this build cannot read.
    /// </para>
    /// </summary>
    public const int SchemaVersion = 2;

    /// <summary>
    /// Registers every configured cache instance (package request 13/31) and binds
    /// <see cref="ITypedCache"/> to the default one behind <see cref="MeteredTypedCache"/>. Without a Redis
    /// connection string the cache is L1-only: correct on a single replica, and the reason tag-based
    /// invalidation goes through the backplane rather than a local dictionary.
    /// <para>
    /// D-052: <c>AddTypedCache</c> also registers <see cref="EF.Common.Contracts.IDistributedLock"/> from the
    /// same "is Redis configured" answer that decides L1-only versus L1+L2, so a caller cannot accidentally
    /// get a process-local lock on a multi-replica deployment.
    /// </para>
    /// </summary>
    public static IServiceCollection AddTaskFlowCaching(this IServiceCollection services, IConfiguration config)
    {
        services.AddTypedCache(config, SectionName);
        services.AddSingleton<CacheMeter>();

        // AddTypedCache binds ITypedCache to a bare TypedCache. Re-registering it here is the only seam the
        // package offers for the two things it does not own: the TaskFlow.Cache meter, and the settings that
        // are properties of the deployment rather than of configuration.
        services.Replace(ServiceDescriptor.Singleton<ITypedCache>(sp =>
        {
            var settings = ApplyTaskFlowDefaults(
                sp.GetRequiredService<CacheSettings>(),
                sp.GetRequiredService<IHostEnvironment>().EnvironmentName);

            return new MeteredTypedCache(
                new TypedCache(sp.GetRequiredService<IFusionCacheProvider>(), settings),
                sp.GetRequiredService<CacheMeter>());
        }));

        // FusionCache's own hit/miss/latency instrumentation, registered here rather than in the host's
        // telemetry setup so it arrives with the cache and cannot be forgotten by a host that adds caching.
        // AddTypedCache does not register it - it takes no OpenTelemetry dependency.
        services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddFusionCacheInstrumentation());

        return services;
    }

    /// <summary>
    /// Stamps the settings a configuration section cannot carry, and the profile durations that are code
    /// decisions rather than deployment knobs. Called from the <see cref="ITypedCache"/> factory, which is the
    /// only reader of these three members, so the stamp always precedes the read.
    /// </summary>
    /// <param name="settings">The bound settings for the default cache instance.</param>
    /// <param name="environmentName">Deployment environment; becomes the leading key segment.</param>
    /// <returns>The same instance, for chaining.</returns>
    public static CacheSettings ApplyTaskFlowDefaults(CacheSettings settings, string environmentName)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // The environment segment keeps a shared Redis from serving staging entries to production. It is a
        // property of the deployment, not a value the CacheSettings section should be able to get wrong.
        settings.KeyNamespace = environmentName;
        settings.SchemaVersion = SchemaVersion;

        // Category and tag lists: change rarely, expensive to rebuild, refreshed before they expire.
        settings.Profiles.TryAdd(CacheProfiles.Metadata, new CacheProfileOptions
        {
            DurationSeconds = 300,
            DistributedDurationSeconds = 1800,
            FailSafeMaxDurationSeconds = 7200,
            EagerRefreshThreshold = 0.8f
        });

        // Dashboard counts: cheap, visibly wrong when stale, so held for seconds with a soft timeout.
        settings.Profiles.TryAdd(CacheProfiles.Summary, new CacheProfileOptions
        {
            DurationSeconds = 5,
            DistributedDurationSeconds = 15,
            FailSafeMaxDurationSeconds = 60,
            FactorySoftTimeoutMilliseconds = 500
        });

        return settings;
    }

    /// <summary>
    /// Registers the per-tenant rate-limiter factory. The host supplies the ASP.NET partitions; the tier
    /// lookup, the Redis budget, and the fail-open policy live here.
    /// </summary>
    public static IServiceCollection AddTaskFlowRateLimiting(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<RateLimitingSettings>(config.GetSection(RateLimitingSettings.ConfigSectionName));
        services.AddSingleton<RateLimitingMeter>();
        services.AddSingleton<TenantRateLimiterFactory>();
        return services;
    }
}
