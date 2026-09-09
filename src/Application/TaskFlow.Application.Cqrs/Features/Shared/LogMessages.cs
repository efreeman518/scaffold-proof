using Microsoft.Extensions.Logging;
using TaskFlow.Observability;

namespace TaskFlow.Application.Cqrs.Shared;

/// <summary>
/// Source-generated logging methods for the CQRS shared helpers. Using <see cref="LoggerMessageAttribute"/>
/// defers argument evaluation until the log level is enabled, satisfying CA1873 and avoiding needless work.
/// </summary>
internal static partial class LogMessages
{
    /// <summary>Logs that a search operation was cancelled by the client.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationCqrsBase + 1, Level = LogLevel.Debug, Message = "{Operation} search cancelled by client.")]
    public static partial void SearchCancelled(this ILogger logger, string operation);

    /// <summary>
    /// Logs a save failure from <see cref="CqrsHandlerSupport.TrySaveAsync"/>, carrying the caller-supplied
    /// error message and args verbatim (the message template itself is fixed; only the values vary per call site).
    /// </summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationCqrsBase + 2, Level = LogLevel.Error, Message = "{ErrorMessage} {Args}")]
    public static partial void SaveFailed(this ILogger logger, Exception exception, string errorMessage, object?[] args);

    /// <summary>Logs a blob upload failure while creating an Attachment.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationCqrsBase + 3, Level = LogLevel.Error, Message = "Error uploading blob for Attachment {FileName}")]
    public static partial void AttachmentBlobUploadFailed(this ILogger logger, Exception exception, string fileName);

    /// <summary>Logs a blob delete failure after an Attachment was deleted.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationCqrsBase + 4, Level = LogLevel.Warning, Message = "Failed to delete blob for Attachment {Id}")]
    public static partial void AttachmentBlobDeleteFailed(this ILogger logger, Exception exception, Guid id);
}
