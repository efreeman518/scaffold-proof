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

    /// <summary>Logs that forwarded gateway claims could not be parsed.</summary>
    [LoggerMessage(EventId = LogEventIds.ApiBase + 6, Level = LogLevel.Warning, Message = "Failed to parse forwarded gateway claims from {HeaderName}")]
    public static partial void GatewayClaimsParseFailed(this ILogger logger, Exception exception, string headerName);

    /// <summary>Logs a client-error (4xx, excluding cancellation) response.</summary>
    [LoggerMessage(EventId = LogEventIds.ApiBase + 7, Level = LogLevel.Warning, Message = "Client error {StatusCode}: {ExceptionType} - {Message}")]
    public static partial void ClientError(this ILogger logger, Exception exception, int statusCode, string exceptionType, string message);

    /// <summary>Logs an unhandled (5xx) exception.</summary>
    [LoggerMessage(EventId = LogEventIds.ApiBase + 8, Level = LogLevel.Error, Message = "Unhandled exception: {ExceptionType} - {Message}")]
    public static partial void UnhandledException(this ILogger logger, Exception exception, string exceptionType, string message);

    /// <summary>Logs an If-Match wildcard override (GR-16), the one way a caller can overwrite a concurrent change on purpose.</summary>
    [LoggerMessage(EventId = LogEventIds.ApiBase + 9, Level = LogLevel.Information, Message = "If-Match wildcard override on {Method} {Path}")]
    public static partial void IfMatchWildcardOverride(this ILogger logger, string method, PathString path);

    /// <summary>Logs an If-Match precondition failure (412).</summary>
    [LoggerMessage(EventId = LogEventIds.ApiBase + 10, Level = LogLevel.Warning, Message = "Precondition failed on {Method} {Path}: expected version {Expected}, current {Current}")]
    public static partial void IfMatchPreconditionFailed(this ILogger logger, string method, PathString path, long? expected, long current);
}
