using Microsoft.Extensions.Logging;
using TaskFlow.Observability;

namespace TaskFlow.Functions;

/// <summary>
/// Source-generated logging methods for the Functions host. Using <see cref="LoggerMessageAttribute"/>
/// defers argument evaluation until the log level is enabled, satisfying CA1873 and avoiding needless work.
/// </summary>
internal static partial class LogMessages
{
    /// <summary>Logs that a blob-trigger attachment is being processed.</summary>
    [LoggerMessage(EventId = LogEventIds.FunctionsBase + 1, Level = LogLevel.Information, Message = "Blob trigger: processing attachment '{Name}', size {Size} bytes")]
    public static partial void BlobProcessing(this ILogger logger, string name, long size);

    /// <summary>Logs that a category was created via the category trigger.</summary>
    [LoggerMessage(EventId = LogEventIds.FunctionsBase + 2, Level = LogLevel.Information, Message = "CreateCategory created {CategoryId}")]
    public static partial void CategoryCreated(this ILogger logger, Guid? categoryId);

    /// <summary>Logs that a health check was requested.</summary>
    [LoggerMessage(EventId = LogEventIds.FunctionsBase + 3, Level = LogLevel.Information, Message = "Health check requested at {UtcNow}")]
    public static partial void HealthCheckRequested(this ILogger logger, DateTime utcNow);

    /// <summary>Logs that the task API proxy was invoked.</summary>
    [LoggerMessage(EventId = LogEventIds.FunctionsBase + 4, Level = LogLevel.Information, Message = "TaskApiProxy invoked at {UtcNow}")]
    public static partial void TaskApiProxyInvoked(this ILogger logger, DateTime utcNow);

    /// <summary>Logs a delivery that was dead-lettered because it could not be understood.</summary>
    [LoggerMessage(EventId = LogEventIds.FunctionsBase + 5, Level = LogLevel.Warning, Message = "Consumer {Consumer} dead-lettered message {MessageId}: {Reason}")]
    public static partial void EnvelopeRejected(this ILogger logger, string consumer, string? messageId, string reason);

    /// <summary>Logs that a CreateCategory function invocation failed application validation.</summary>
    [LoggerMessage(EventId = LogEventIds.FunctionsBase + 6, Level = LogLevel.Warning, Message = "CreateCategory failed for request {Name}")]
    public static partial void CreateCategoryFailed(this ILogger logger, string name);
}
