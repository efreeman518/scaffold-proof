using EF.Cache;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using StackExchange.Redis;
using TaskFlow.Application.Contracts.Caching;

namespace TaskFlow.Infrastructure.Caching;

/// <summary>
/// Composition for the cache tier. Lives here rather than in the Bootstrapper so the EF.Cache and FusionCache
/// packages stay behind this project's boundary.
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
    /// Registers every configured cache instance (EF.Cache <c>AddTypedCache</c>) and binds <see cref="ITypedCache"/>
    /// to the default one. Without a Redis connection string the cache is L1-only: correct on a single replica, and
    /// the reason tag-based invalidation goes through the backplane rather than a local dictionary.
    /// <list type="bullet">
    /// <item>S7: with Redis configured, the package registers one shared <c>IConnectionMultiplexer</c>
    /// (<c>AbortOnConnectFail</c> forced false) that L2, the backplane, the D-052 distributed lock, the Redis health
    /// check, the Data Protection key ring and the tenant rate limiter all use.</item>
    /// <item>S8: the key namespace defaults to the host environment, so a shared Redis never serves one
    /// environment's entries to another; the <c>ef.cache.degraded</c> / <c>ef.cache.invalidations</c> instruments and
    /// FusionCache's own meter and source are exported here, so they arrive with the cache.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddTaskFlowCaching(this IServiceCollection services, IConfiguration config)
    {
        services.AddTypedCache(config, SectionName, configure: ApplyCodeDefaults);

        if (config.GetValue("OpenTelemetry:MetricsEnabled", true))
        {
            services.AddOpenTelemetry()
                .WithMetrics(metrics => metrics.AddMeter([.. CacheTelemetry.MeterNames()]))
                .WithTracing(tracing => tracing.AddSource([.. CacheTelemetry.ActivitySourceNames]));
        }

        return services;
    }

    /// <summary>
    /// True when <see cref="AddTaskFlowCaching"/> registered the shared default <see cref="IConnectionMultiplexer"/>,
    /// that is when the default cache instance has Redis configured. The Redis health check and the Redis rate
    /// limiter backend use this one answer, so neither can resolve a multiplexer the cache did not register.
    /// </summary>
    public static bool HasSharedRedis(this IServiceCollection services) =>
        services.Any(d => d.ServiceType == typeof(IConnectionMultiplexer) && !d.IsKeyedService);

    /// <summary>
    /// The settings that are code decisions rather than deployment knobs: the schema version, and the profile
    /// durations. A profile a deployment configures wins over the default here.
    /// </summary>
    private static void ApplyCodeDefaults(CacheSettings settings)
    {
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
    }
}
