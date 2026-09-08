using Microsoft.AspNetCore.Http;
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

    /// <summary>Logs that a request was cancelled by the client.</summary>
    [LoggerMessage(EventId = LogEventIds.ApiBase + 5, Level = LogLevel.Information, Message = "Request cancelled by client: {Path}")]
    public static partial void RequestCancelledByClient(this ILogger logger, PathString path);
}
