using Microsoft.Extensions.Logging;
using TaskFlow.Observability;

namespace TaskFlow.Infrastructure.Caching;

/// <summary>
/// Source-generated logging methods for the Infrastructure.Caching layer. Using
/// <see cref="LoggerMessageAttribute"/> defers argument evaluation until the log level is enabled,
/// satisfying CA1873 and avoiding needless work.
/// </summary>
internal static partial class LogMessages
{
    /// <summary>Logs that a rate limiter partition is running in-process because no Redis connection is configured.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureCachingBase + 1, Level = LogLevel.Debug, Message = "No Redis connection configured: rate limiting {Partition} in process.")]
    public static partial void RateLimiterInProcessFallback(this ILogger logger, string partition);

    /// <summary>Logs that the distributed rate limiter backend is unavailable and the request was admitted unmetered.</summary>
    [LoggerMessage(EventId = LogEventIds.InfrastructureCachingBase + 2, Level = LogLevel.Warning, Message = "Rate limiter backend unavailable; admitting the request unmetered.")]
    public static partial void RateLimiterBackendUnavailable(this ILogger logger, Exception exception);
}
