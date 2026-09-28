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
    private readonly IConnectionMultiplexer? _redis;

    /// <summary>
    /// Initializes the factory and connects to Redis once. <c>AbortOnConnectFail = false</c> is forced whatever
    /// the connection string says (Aspire and compose strings leave it at the throwing default): an unreachable
    /// Redis then yields a multiplexer that keeps reconnecting in the background instead of an exception.
    /// A throwing connect used to be cached by a <c>Lazy</c> and raised from the limiter constructor, outside
    /// <see cref="FailOpenRateLimiter"/>, so one Redis blip at the first partition 500ed until restart.
    /// </summary>
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
        if (string.IsNullOrWhiteSpace(connectionString))
            return;

        var options = ConfigurationOptions.Parse(connectionString);
        options.AbortOnConnectFail = false;
        _redis = ConnectionMultiplexer.Connect(options);
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

        if (_redis is not { } redis)
        {
            _logger.RateLimiterInProcessFallback(partitionKey);
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
            ConnectionMultiplexerFactory = () => redis
        });

        return new FailOpenRateLimiter(redisLimiter, _meter, _logger);
    }
}
