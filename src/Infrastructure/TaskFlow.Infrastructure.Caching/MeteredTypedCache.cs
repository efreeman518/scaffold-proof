using EF.Cache;
using TaskFlow.Observability.Meters;

namespace TaskFlow.Infrastructure.Caching;

/// <summary>
/// Adds TaskFlow's cache-health telemetry to <see cref="EF.Cache.TypedCache"/> and forwards everything else.
/// <para>
/// Two states are silent by design and are the reason this wrapper exists. A degraded mode - fail-safe
/// returning stale data, or an open circuit running L1-only so replicas quietly disagree - only becomes
/// visible if something subscribes to <see cref="ITypedCache.Degraded"/>. And an eviction requested by a
/// write is the other half of the picture: without it, a stale read cannot be told from a missing
/// invalidation. The package owns neither meter, and deliberately so.
/// </para>
/// </summary>
/// <param name="inner">The package cache doing the work.</param>
/// <param name="meter">TaskFlow.Cache meter.</param>
public sealed class MeteredTypedCache(ITypedCache inner, CacheMeter meter) : ITypedCache
{
    private readonly ITypedCache _inner = Subscribe(inner, meter);

    /// <inheritdoc />
    public string Name => _inner.Name;

    /// <inheritdoc />
    public event EventHandler<CacheDegradedEventArgs>? Degraded
    {
        add => _inner.Degraded += value;
        remove => _inner.Degraded -= value;
    }

    /// <inheritdoc />
    public string RenderKey(CacheKey key) => _inner.RenderKey(key);

    /// <inheritdoc />
    public Task<T> GetOrSetAsync<T>(
        CacheKey key,
        Func<CancellationToken, Task<T>> factory,
        string? profile = null,
        IReadOnlyList<string>? tags = null,
        CancellationToken ct = default) =>
        _inner.GetOrSetAsync(key, factory, profile, tags, ct);

    /// <inheritdoc />
    public Task SetAsync<T>(
        CacheKey key,
        T value,
        string? profile = null,
        IReadOnlyList<string>? tags = null,
        CancellationToken ct = default) =>
        _inner.SetAsync(key, value, profile, tags, ct);

    /// <inheritdoc />
    public Task<T?> GetOrDefaultAsync<T>(CacheKey key, CancellationToken ct = default) =>
        _inner.GetOrDefaultAsync<T>(key, ct);

    /// <inheritdoc />
    public Task RemoveAsync(CacheKey key, CancellationToken ct = default)
    {
        meter.RecordInvalidation("key");
        return _inner.RemoveAsync(key, ct);
    }

    /// <inheritdoc />
    public Task RemoveByTagAsync(string tag, CancellationToken ct = default)
    {
        meter.RecordInvalidation(Scope(tag));
        return _inner.RemoveByTagAsync(tag, ct);
    }

    /// <summary>Wires the degraded-mode events onto the meter as the wrapper is built.</summary>
    private static ITypedCache Subscribe(ITypedCache inner, CacheMeter meter)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentNullException.ThrowIfNull(meter);

        // The handler runs on FusionCache's own event thread, so it does the least possible: one counter add.
        inner.Degraded += (_, e) => meter.RecordDegraded(Reason(e.Reason));
        return inner;
    }

    /// <summary>Metric dimension for a degraded mode; the names predate the package and are kept stable.</summary>
    private static string Reason(CacheDegradedReason reason) => reason switch
    {
        CacheDegradedReason.FailSafe => "fail_safe",
        CacheDegradedReason.DistributedCacheCircuitBreaker => "circuit_breaker",
        CacheDegradedReason.BackplaneCircuitBreaker => "backplane_circuit_breaker",
        _ => "unknown"
    };

    /// <summary>Reduces a tag to the metric dimension: the entity type, or "tenant" for a whole-tenant evict.</summary>
    private static string Scope(string tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        var lastSeparator = tag.LastIndexOf(':');
        return lastSeparator > 1 ? tag[(lastSeparator + 1)..] : "tenant";
    }
}
