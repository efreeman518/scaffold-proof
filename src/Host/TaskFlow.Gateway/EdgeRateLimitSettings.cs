namespace TaskFlow.Gateway;

/// <summary>
/// Edge protection budget (D-050). This is not the Api's tenant limiter and does not replace it: the tenant
/// limiter protects tenants from each other after authentication, this one protects the gateway process from
/// unauthenticated traffic that has not reached the Api yet.
/// <para>
/// Ceiling: the partitions are in-process, so the real allowance is this budget multiplied by the replica
/// count. That is the right trade while the gateway runs on a handful of replicas; when the count makes
/// cross-replica accounting matter, swap the partition factories for the Redis-backed sliding-window shape
/// already used by <c>TenantRateLimiterFactory</c> in <c>TaskFlow.Infrastructure.Caching</c>.
/// </para>
/// </summary>
public sealed class EdgeRateLimitSettings
{
    /// <summary>Configuration section this binds from.</summary>
    public const string ConfigSectionName = "RateLimiting:Edge";

    /// <summary>Whether the edge limiter is applied at all. False leaves the global limiter unset.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Bucket capacity and the tokens restored each replenishment period, per client IP.</summary>
    public int TokensPerPeriod { get; set; } = 200;

    /// <summary>Replenishment period in seconds.</summary>
    public int ReplenishmentSeconds { get; set; } = 1;

    /// <summary>
    /// Queued requests per partition. Zero on purpose: at the edge, shedding immediately with a Retry-After is
    /// better than holding a connection open to serve it late.
    /// </summary>
    public int QueueLimit { get; set; }

    /// <summary>Concurrent proxied requests across all clients, the backstop against a slow downstream.</summary>
    public int MaxConcurrentRequests { get; set; } = 1000;
}
