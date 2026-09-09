using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using StackExchange.Redis;

namespace TaskFlow.Bootstrapper.HealthChecks;

/// <summary>
/// Pings the Redis behind the distributed cache and the rate limiter. Degraded rather than Unhealthy when the
/// ping fails: both callers keep serving without Redis - the cache falls back to L1 and the limiter fails
/// open - so a dead Redis is a correctness and capacity problem to page on, not a reason to pull the
/// instance out of rotation.
/// </summary>
public sealed class RedisCacheHealthCheck(IConfiguration config) : IHealthCheck
{
    private const string ConnectionName = "Redis1";

    /// <summary>Provides the check health operation for redis cache health check.</summary>
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var connectionString = config.GetConnectionString(ConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
            return HealthCheckResult.Healthy("Redis is not configured; cache and limiter run in process.");

        try
        {
            // A fresh connection per check rather than the shared multiplexer: the multiplexer reconnects in
            // the background and would report healthy from cached state while every command times out.
            await using var connection = await ConnectionMultiplexer.ConnectAsync(connectionString)
                .ConfigureAwait(false);
            var latency = await connection.GetDatabase().PingAsync().ConfigureAwait(false);

            return HealthCheckResult.Healthy(
                $"Redis reachable in {latency.TotalMilliseconds:F0}ms.",
                new Dictionary<string, object> { ["latency_ms"] = latency.TotalMilliseconds });
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Degraded(
                "Redis unreachable: cache is L1-only and the rate limiter is failing open.", ex);
        }
    }
}
