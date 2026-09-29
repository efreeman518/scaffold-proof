using Microsoft.Extensions.Logging;
using TaskFlow.Observability;

namespace TaskFlow.Api;

/// <summary>
/// Source-generated logging methods for the API host (startup pipeline and middleware). Using
/// <see cref="LoggerMessageAttribute"/> defers argument evaluation until the log level is enabled,
/// satisfying CA1873 and avoiding needless work.
/// </summary>
internal static partial class LogMessages
{
    /// <summary>Logs application startup.</summary>
    [LoggerMessage(EventId = LogEventIds.ApiBase + 1, Level = LogLevel.Information, Message = "{AppName} {Environment} - Startup.")]
    public static partial void Startup(this ILogger logger, string appName, string environment);

    /// <summary>Logs an unexpected host termination.</summary>
    [LoggerMessage(EventId = LogEventIds.ApiBase + 3, Level = LogLevel.Critical, Message = "{AppName} {Environment} - Host terminated unexpectedly.")]
    public static partial void HostTerminated(this ILogger logger, Exception exception, string appName, string environment);

    /// <summary>Logs application shutdown.</summary>
    [LoggerMessage(EventId = LogEventIds.ApiBase + 4, Level = LogLevel.Information, Message = "{AppName} {Environment} - Ending application.")]
    public static partial void EndingApplication(this ILogger logger, string appName, string environment);

    /// <summary>Logs that forwarded gateway claims could not be parsed.</summary>
    [LoggerMessage(EventId = LogEventIds.ApiBase + 6, Level = LogLevel.Warning, Message = "Failed to parse forwarded gateway claims from {HeaderName}")]
    public static partial void GatewayClaimsParseFailed(this ILogger logger, Exception exception, string headerName);
}
