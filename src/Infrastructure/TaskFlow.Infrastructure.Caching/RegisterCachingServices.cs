using MessagePack;
using MessagePack.Resolvers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Application.Contracts.Locking;
using TaskFlow.Application.Models.Serialization;
using TaskFlow.Observability.Meters;
using OpenTelemetry.Metrics;
using ZiggyCreatures.Caching.Fusion;
using TaskFlow.Infrastructure.Caching.Locking;
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
            var fcBuilder = services.AddFusionCache(settings.Name);
            ApplySerializer(fcBuilder, settings);
            fcBuilder
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

        // D-052: the same "is Redis configured" answer that decides L1-only vs L1+L2 decides whether the
        // startup lock is real. Deciding it here rather than at each call site is the point: a caller cannot
        // accidentally get a process-local lock on a multi-replica deployment.
        var lockConnStr = !string.IsNullOrEmpty(defaultSettings.RedisConnectionStringName)
            ? config.GetConnectionString(defaultSettings.RedisConnectionStringName)
            : null;

        if (string.IsNullOrEmpty(lockConnStr))
        {
            services.AddSingleton<IDistributedLock, InProcessDistributedLock>();
        }
        else
        {
            services.AddSingleton<IDistributedLock>(_ => new RedisDistributedLock(lockConnStr));
        }

        // FusionCache's own hit/miss/latency instrumentation, registered here rather than in the host's
        // telemetry setup so it arrives with the cache and cannot be forgotten by a host that adds caching.
        services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddFusionCacheInstrumentation());

        return services;
    }

    /// <summary>
    /// D-048: picks the serializer for one named cache. JSON stays the default because it is the format
    /// every existing entry is written in and the one a human can read out of Redis during an incident;
    /// MessagePack is opt-in per cache for the entries where the size and CPU of the L2 hop matter.
    ///
    /// The MessagePack arm uses the CONTRACTLESS resolver, which serializes by property name rather than
    /// by an attribute-assigned key index. That is what keeps this a deployment setting instead of a code
    /// change: no [MessagePackObject]/[Key] attributes on the DTOs, no build-time formatter generation,
    /// and the same tolerance for an added property that the JSON arm has. LZ4 block-array compression is
    /// on because cached snapshots are lists of similar records, which is the shape it pays off on.
    ///
    /// Scope note: this is the L2 cache value only. The queue payload deliberately stays JSON (D-048) -
    /// a binary body loses broker-side filtering, cross-language consumers, and the ability to read a
    /// dead-lettered message without a decoder.
    ///
    /// Switching an existing cache from Json to MessagePack does not migrate anything: entries written in
    /// the other format fail to deserialize, and FusionCache treats that as a miss and refactories them.
    /// Bump <see cref="CacheSettings.SchemaVersion"/> alongside the switch to retire them by key instead.
    /// </summary>
    private static void ApplySerializer(IFusionCacheBuilder builder, CacheSettings settings)
    {
        switch (settings.Serializer)
        {
            case CacheSerializer.MessagePack:
                builder.WithNeueccMessagePackSerializer(
                    MessagePackSerializerOptions.Standard
                        .WithResolver(ContractlessStandardResolver.Instance)
                        .WithCompression(MessagePackCompression.Lz4BlockArray));
                break;
            case CacheSerializer.Json:
                builder.WithSystemTextJsonSerializer(CacheSerializerOptions());
                break;
            default:
                throw new InvalidOperationException(
                    $"CacheSettings:{settings.Name}:Serializer has unsupported value '{settings.Serializer}'.");
        }
    }

    /// <summary>
    /// Serializer options for every named cache. D-048: the generated resolver goes first and the
    /// reflection resolver stays behind it, so cached TaskFlow DTOs skip per-entry reflection metadata
    /// while third-party cached shapes still serialize.
    ///
    /// The stored format does not change. These options declare no naming policy, and the naming policy
    /// is applied from the options (not baked into the generated context), so entries keep the PascalCase
    /// names the reflection serializer wrote - an L1/L2 entry written by the previous build still reads
    /// back after a rolling deploy. ReferenceHandler.Preserve is likewise an options-level setting and
    /// still applies, which matters because cached aggregates (TaskItemDto.SubTasks) can self-reference.
    /// </summary>
    private static JsonSerializerOptions CacheSerializerOptions()
    {
        var options = new JsonSerializerOptions { ReferenceHandler = ReferenceHandler.Preserve };
        options.TypeInfoResolverChain.Insert(0, TaskFlowJsonContext.Default);
        options.TypeInfoResolverChain.Add(new DefaultJsonTypeInfoResolver());
        return options;
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
