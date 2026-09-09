using Microsoft.Extensions.Hosting;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Observability.Meters;
using ZiggyCreatures.Caching.Fusion;

namespace TaskFlow.Infrastructure.Caching;

/// <summary>
/// The single <see cref="ITaskFlowCache"/> implementation, over one named FusionCache instance (L1 memory +
/// L2 Redis + backplane). It owns key rendering, the tag set an entry carries, and the profile-to-entry-options
/// mapping, so callers never construct a cache key or an expiry and cannot drift from each other.
/// </summary>
public sealed class FusionTaskFlowCache : ITaskFlowCache
{
    private readonly IFusionCache _cache;
    private readonly CacheSettings _settings;
    private readonly CacheMeter _meter;
    private readonly string _prefix;

    /// <summary>Initializes the cache over the named FusionCache instance and subscribes to its degraded-mode events.</summary>
    public FusionTaskFlowCache(
        IFusionCacheProvider cacheProvider,
        CacheSettings settings,
        CacheMeter meter,
        IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(cacheProvider);
        _settings = settings;
        _meter = meter;
        _cache = cacheProvider.GetCache(settings.Name);

        // The environment segment keeps a shared Redis from serving staging entries to production; the schema
        // version retires every entry at once when a snapshot's shape changes.
        _prefix = $"{environment.EnvironmentName}:{settings.SchemaVersion}:";

        // Degraded modes are silent by design: fail-safe returns stale data instead of throwing, and an open
        // circuit runs L1-only so replicas quietly disagree. Counting them is the only way either is visible.
        _cache.Events.FailSafeActivate += (_, _) => _meter.RecordDegraded("fail_safe");
        _cache.Events.Distributed.CircuitBreakerChange += (_, e) =>
        {
            if (!e.IsClosed) _meter.RecordDegraded("circuit_breaker");
        };
        _cache.Events.Backplane.CircuitBreakerChange += (_, e) =>
        {
            if (!e.IsClosed) _meter.RecordDegraded("backplane_circuit_breaker");
        };
    }

    /// <inheritdoc />
    public async Task<T> GetOrSetAsync<T>(
        CacheKey key,
        Func<CancellationToken, Task<T>> factory,
        CacheProfile profile,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(factory);

        return await _cache.GetOrSetAsync<T>(
            Render(key),
            (_, token) => factory(token),
            default,
            EntryOptions(profile),
            TagsFor(key),
            ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveByTagAsync(string tag, CancellationToken ct = default)
    {
        _meter.RecordInvalidation(Scope(tag));
        await _cache.RemoveByTagAsync(tag, token: ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task RemoveAsync(CacheKey key, CancellationToken ct = default)
    {
        _meter.RecordInvalidation("key");
        await _cache.RemoveAsync(Render(key), token: ct).ConfigureAwait(false);
    }

    /// <summary><c>{env}:{schemaVersion}:{tenantId:N}:{kind}[:{discriminator}]</c>.</summary>
    public string Render(CacheKey key) =>
        key.Discriminator is null
            ? $"{_prefix}{key.TenantId:N}:{key.Kind}"
            : $"{_prefix}{key.TenantId:N}:{key.Kind}:{key.Discriminator}";

    /// <summary>
    /// The tags one entry carries: its tenant, plus every entity type whose mutation invalidates it. Writers
    /// evict by what they changed, so a new snapshot only has to declare what it is built from here.
    /// </summary>
    public static IReadOnlyList<string> TagsFor(CacheKey key) => key.Kind switch
    {
        CacheKind.TaskSummary =>
            [CacheTags.Tenant(key.TenantId), CacheTags.Entity(key.TenantId, CacheTags.TaskItem)],
        CacheKind.TaskMetadata =>
            [
                CacheTags.Tenant(key.TenantId),
                CacheTags.Entity(key.TenantId, CacheTags.Category),
                CacheTags.Entity(key.TenantId, CacheTags.Tag)
            ],
        _ => [CacheTags.Tenant(key.TenantId)]
    };

    /// <summary>Maps a profile onto FusionCache entry options, falling back to the instance defaults.</summary>
    private FusionCacheEntryOptions EntryOptions(CacheProfile profile)
    {
        var p = profile == CacheProfile.Metadata ? _settings.Profiles.Metadata : _settings.Profiles.Summary;

        var options = new FusionCacheEntryOptions
        {
            Duration = TimeSpan.FromSeconds(p.DurationSeconds),
            DistributedCacheDuration = TimeSpan.FromSeconds(p.DistributedDurationSeconds),
            IsFailSafeEnabled = true,
            FailSafeMaxDuration = TimeSpan.FromSeconds(p.FailSafeMaxDurationSeconds),
            FailSafeThrottleDuration = TimeSpan.FromSeconds(_settings.FailSafeThrottleDurationSeconds),
            JitterMaxDuration = TimeSpan.FromSeconds(_settings.JitterMaxDurationSeconds),
            FactoryHardTimeout = TimeSpan.FromSeconds(_settings.FactoryHardTimeoutSeconds),
            // Every entry declares a size because the L1 memory cache is capped; without it the cap is ignored.
            Size = 1
        };

        if (p.EagerRefreshThreshold is { } threshold)
            options.EagerRefreshThreshold = threshold;

        // A soft timeout only helps when there is something stale to return, which fail-safe guarantees.
        options.FactorySoftTimeout = p.FactorySoftTimeoutMilliseconds is { } soft
            ? TimeSpan.FromMilliseconds(soft)
            : TimeSpan.FromSeconds(_settings.FactorySoftTimeoutSeconds);

        return options;
    }

    /// <summary>Reduces a tag to the metric dimension: the entity type, or "tenant" for a whole-tenant evict.</summary>
    private static string Scope(string tag)
    {
        var lastSeparator = tag.LastIndexOf(':');
        return lastSeparator > 1 ? tag[(lastSeparator + 1)..] : "tenant";
    }
}
