using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using RedisRateLimiting;
using StackExchange.Redis;
using System.Threading.RateLimiting;
using TaskFlow.Observability.Meters;

namespace TaskFlow.Infrastructure.Caching.RateLimiting;

/// <summary>
/// Builds the per-tenant limiter the API partitions on. The in-process fixed-window limiter it replaces
/// counted per replica, so a tenant's real allowance was the configured limit multiplied by the replica count
/// and changed every time the API scaled. A sliding window in Redis is one shared budget across replicas, and
/// sliding rather than fixed so a tenant cannot spend two windows' worth of requests across a boundary.
/// <para>
/// Without a Redis connection string the limiter stays in-process: correct on one replica, and the same
/// per-replica caveat otherwise. That is stated at the registration site rather than silently assumed.
/// </para>
/// </summary>
public sealed class TenantRateLimiterFactory
{
    private readonly RateLimitingSettings _settings;
    private readonly RateLimitingMeter _meter;
    private readonly ILogger<TenantRateLimiterFactory> _logger;
    private readonly Lazy<IConnectionMultiplexer>? _redis;

    /// <summary>Initializes the factory, connecting to Redis lazily so startup does not block on it.</summary>
    public TenantRateLimiterFactory(
        IOptions<RateLimitingSettings> settings,
        IConfiguration config,
        RateLimitingMeter meter,
        ILogger<TenantRateLimiterFactory> logger)
    {
        _settings = settings.Value;
        _meter = meter;
        _logger = logger;

        var connectionString = config.GetConnectionString(_settings.RedisConnectionStringName);
        _redis = string.IsNullOrWhiteSpace(connectionString)
            ? null
            : new Lazy<IConnectionMultiplexer>(() => ConnectionMultiplexer.Connect(connectionString));
    }

    /// <summary>True when the limiter budget is shared across replicas.</summary>
    public bool IsDistributed => _redis is not null;

    /// <summary>Tier name for a tenant, used as the rejection metric dimension.</summary>
    public string TierFor(string? tenantId) => _settings.TierFor(tenantId);

    /// <summary>Records a rejection for a tenant's tier.</summary>
    public void RecordRejected(string? tenantId) => _meter.RecordRejected(TierFor(tenantId));

    /// <summary>Builds the limiter for one tenant's interactive budget.</summary>
    public RateLimiter CreateTenantLimiter(string tenantId)
    {
        var allowance = _settings.AllowanceFor(_settings.TierFor(tenantId));
        return Create($"taskflow:rl:tenant:{tenantId}", allowance);
    }

    /// <summary>Builds the limiter for one tenant's export budget, kept apart from the interactive one.</summary>
    public RateLimiter CreateExportLimiter(string tenantId) =>
        Create($"taskflow:rl:export:{tenantId}", _settings.Export);

    /// <summary>Redis sliding window when a connection is configured, in-process sliding window otherwise.</summary>
    private RateLimiter Create(string partitionKey, RateLimitTier allowance)
    {
        var window = TimeSpan.FromSeconds(allowance.WindowSeconds);

        if (_redis is null)
        {
            _logger.LogDebug("No Redis connection configured: rate limiting {Partition} in process.", partitionKey);
            return new SlidingWindowRateLimiter(new SlidingWindowRateLimiterOptions
            {
                PermitLimit = allowance.PermitLimit,
                Window = window,
                SegmentsPerWindow = 6,
                QueueLimit = 0
            });
        }

        var redisLimiter = new RedisSlidingWindowRateLimiter<string>(partitionKey, new RedisSlidingWindowRateLimiterOptions
        {
            PermitLimit = allowance.PermitLimit,
            Window = window,
            ConnectionMultiplexerFactory = () => _redis.Value
        });

        return new FailOpenRateLimiter(redisLimiter, _meter, _logger);
    }
}
