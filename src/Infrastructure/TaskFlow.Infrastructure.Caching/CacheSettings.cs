namespace TaskFlow.Infrastructure.Caching;

/// <summary>
/// One named FusionCache instance. Bound from the <c>CacheSettings</c> array so a host can run more than one
/// cache with different durability, and so the numbers below are deployment settings rather than constants
/// recompiled into the app.
/// </summary>
public class CacheSettings
{
    public string Name { get; set; } = "Default";
    public int DurationMinutes { get; set; } = 30;
    public int DistributedCacheDurationMinutes { get; set; } = 60;
    public int FailSafeMaxDurationMinutes { get; set; } = 120;
    public int FailSafeThrottleDurationSeconds { get; set; } = 1;
    public int JitterMaxDurationSeconds { get; set; } = 10;
    public int FactorySoftTimeoutSeconds { get; set; } = 1;
    public int FactoryHardTimeoutSeconds { get; set; } = 30;
    public float EagerRefreshThreshold { get; set; } = 0.9f;
    public string? RedisConnectionStringName { get; set; }
    public string? BackplaneChannelName { get; set; }

    /// <summary>
    /// Entries per L1 (in-process) cache. Without a size limit the memory cache is bounded only by the host's
    /// memory, which on a container with a hard limit means an OOM kill rather than an eviction.
    /// </summary>
    public long L1SizeLimit { get; set; } = 20_000;

    /// <summary>
    /// How long a distributed-cache failure keeps the circuit open before Redis is tried again. Short: the
    /// point is to stop hammering a dead Redis on every request, not to stay L1-only through a blip.
    /// </summary>
    public int DistributedCacheCircuitBreakerSeconds { get; set; } = 5;

    /// <summary>
    /// Bumped whenever a cached snapshot's shape changes. It is part of every key, so a deployment with a new
    /// shape simply cannot read the old one - no eviction sweep, no version-mismatch deserialization failures.
    /// </summary>
    public int SchemaVersion { get; set; } = 1;

    /// <summary>Per-profile durability. Missing entries fall back to the profile defaults below.</summary>
    public CacheProfileSettings Profiles { get; set; } = new();
}

/// <summary>Durability for each named cache profile.</summary>
public class CacheProfileSettings
{
    /// <summary>Category and tag lists: changes rarely, expensive to rebuild, refreshed before it expires.</summary>
    public CacheProfileOptions Metadata { get; set; } = new()
    {
        DurationSeconds = 300,
        DistributedDurationSeconds = 1800,
        FailSafeMaxDurationSeconds = 7200,
        EagerRefreshThreshold = 0.8f
    };

    /// <summary>Dashboard counts: cheap, visibly wrong when stale, so held for seconds with a soft timeout.</summary>
    public CacheProfileOptions Summary { get; set; } = new()
    {
        DurationSeconds = 5,
        DistributedDurationSeconds = 15,
        FailSafeMaxDurationSeconds = 60,
        FactorySoftTimeoutMilliseconds = 500
    };
}

/// <summary>Durability of one profile. Zero-valued optional members mean "not configured".</summary>
public class CacheProfileOptions
{
    public int DurationSeconds { get; set; }
    public int DistributedDurationSeconds { get; set; }
    public int FailSafeMaxDurationSeconds { get; set; }
    public float? EagerRefreshThreshold { get; set; }
    public int? FactorySoftTimeoutMilliseconds { get; set; }
}
