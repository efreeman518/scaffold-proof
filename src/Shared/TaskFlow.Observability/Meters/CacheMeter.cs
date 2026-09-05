using System.Diagnostics.Metrics;

namespace TaskFlow.Observability.Meters;

/// <summary>
/// Cache health instruments. FusionCache's own OpenTelemetry instrumentation reports hits, misses, and
/// latency; these two report the states that change what the application is actually serving - a fail-safe
/// entry (stale data returned because the factory failed) and an open distributed-cache circuit breaker
/// (running L1-only, so replicas can disagree). Both are silent by design, which is why they are counted.
/// </summary>
public sealed class CacheMeter
{
    public const string MeterName = "TaskFlow.Cache";

    private readonly Meter _meter = new(MeterName, "1.0.0");
    private readonly Counter<long> _degraded;
    private readonly Counter<long> _invalidations;

    /// <summary>Initializes cache meter with required dependencies and default state.</summary>
    public CacheMeter()
    {
        _degraded = _meter.CreateCounter<long>(
            "taskflow.cache.degraded", "event", "Fail-safe activations and distributed-cache circuit-breaker changes.");
        _invalidations = _meter.CreateCounter<long>(
            "taskflow.cache.invalidations", "eviction", "Cache evictions requested by a write, by tag scope.");
    }

    /// <summary>Records one degraded-mode event: <paramref name="reason"/> is fail_safe or circuit_breaker.</summary>
    public void RecordDegraded(string reason) =>
        _degraded.Add(1, new KeyValuePair<string, object?>("reason", reason));

    /// <summary>Records one invalidation. <paramref name="scope"/> is the entity type, or "tenant" / "key".</summary>
    public void RecordInvalidation(string scope) =>
        _invalidations.Add(1, new KeyValuePair<string, object?>("scope", scope));
}
