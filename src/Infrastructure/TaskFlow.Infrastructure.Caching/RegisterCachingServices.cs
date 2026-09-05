using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Observability.Meters;
using OpenTelemetry.Metrics;
using ZiggyCreatures.Caching.Fusion;
using TaskFlow.Infrastructure.Caching.RateLimiting;
using ZiggyCreatures.Caching.Fusion.Backplane.StackExchangeRedis;

namespace TaskFlow.Infrastructure.Caching;

/// <summary>
/// Composition for the cache tier. Lives beside the implementation rather than in the Bootstrapper so the
/// FusionCache and Redis packages stay behind this project's boundary.
/// </summary>
public static class RegisterCachingServices
{
    /// <summary>
    /// Registers every configured FusionCache instance and binds <see cref="ITaskFlowCache"/> to the default one.
    /// Without a Redis connection string the cache is L1-only: correct on a single replica, and the reason
    /// tag-based invalidation goes through the backplane rather than a local dictionary.
    /// </summary>
    public static IServiceCollection AddTaskFlowCaching(this IServiceCollection services, IConfiguration config)
    {
        List<CacheSettings> cacheSettings = [];
        config.GetSection("CacheSettings").Bind(cacheSettings);

        if (cacheSettings.Count == 0)
        {
            cacheSettings.Add(new CacheSettings { Name = AppConstants.DEFAULT_CACHE });
        }

        foreach (var settings in cacheSettings)
        {
            var fcBuilder = services.AddFusionCache(settings.Name)
                .WithSystemTextJsonSerializer(new JsonSerializerOptions
                {
                    ReferenceHandler = ReferenceHandler.Preserve
                })
                .WithCacheKeyPrefix($"{settings.Name}:")
                // Own memory cache per named instance with a hard entry cap: a shared, unbounded L1 is how a
                // container with a memory limit gets OOM-killed instead of evicting.
                .WithMemoryCache(new MemoryCache(new MemoryCacheOptions { SizeLimit = settings.L1SizeLimit }))
                .WithOptions(options =>
                {
                    options.DistributedCacheCircuitBreakerDuration =
                        TimeSpan.FromSeconds(settings.DistributedCacheCircuitBreakerSeconds);
                    options.BackplaneCircuitBreakerDuration =
                        TimeSpan.FromSeconds(settings.DistributedCacheCircuitBreakerSeconds);
                })
                .WithDefaultEntryOptions(new FusionCacheEntryOptions
                {
                    Duration = TimeSpan.FromMinutes(settings.DurationMinutes),
                    DistributedCacheDuration = TimeSpan.FromMinutes(settings.DistributedCacheDurationMinutes),
                    IsFailSafeEnabled = true,
                    FailSafeMaxDuration = TimeSpan.FromMinutes(settings.FailSafeMaxDurationMinutes),
                    FailSafeThrottleDuration = TimeSpan.FromSeconds(settings.FailSafeThrottleDurationSeconds),
                    JitterMaxDuration = TimeSpan.FromSeconds(settings.JitterMaxDurationSeconds),
                    FactorySoftTimeout = TimeSpan.FromSeconds(settings.FactorySoftTimeoutSeconds),
                    FactoryHardTimeout = TimeSpan.FromSeconds(settings.FactoryHardTimeoutSeconds),
                    EagerRefreshThreshold = settings.EagerRefreshThreshold,
                    Size = 1
                });

            var redisConnStr = !string.IsNullOrEmpty(settings.RedisConnectionStringName)
                ? config.GetConnectionString(settings.RedisConnectionStringName)
                : null;

            if (!string.IsNullOrEmpty(redisConnStr))
            {
                fcBuilder
                    .WithDistributedCache(new RedisCache(new RedisCacheOptions
                    {
                        Configuration = redisConnStr
                    }))
                    .WithBackplane(new RedisBackplane(new RedisBackplaneOptions
                    {
                        Configuration = redisConnStr
                    }));
            }
        }

        var defaultSettings = cacheSettings.Find(s => s.Name == AppConstants.DEFAULT_CACHE) ?? cacheSettings[0];
        services.AddSingleton(defaultSettings);
        services.AddSingleton<CacheMeter>();
        services.AddSingleton<ITaskFlowCache, FusionTaskFlowCache>();

        // FusionCache's own hit/miss/latency instrumentation, registered here rather than in the host's
        // telemetry setup so it arrives with the cache and cannot be forgotten by a host that adds caching.
        services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddFusionCacheInstrumentation());

        return services;
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
