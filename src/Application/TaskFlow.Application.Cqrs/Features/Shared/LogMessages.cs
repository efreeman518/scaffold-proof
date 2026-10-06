using Microsoft.Extensions.Logging;
using TaskFlow.Observability;

namespace TaskFlow.Application.Cqrs.Shared;

/// <summary>
/// Source-generated logging methods for the CQRS shared helpers. Using <see cref="LoggerMessageAttribute"/>
/// defers argument evaluation until the log level is enabled, satisfying CA1873 and avoiding needless work.
/// </summary>
internal static partial class LogMessages
{
    /// <summary>
    /// Logs a save failure from <see cref="CqrsHandlerSupport.TrySaveAsync"/>, carrying the caller-supplied
    /// error message and args verbatim (the message template itself is fixed; only the values vary per call site).
    /// </summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationCqrsBase + 2, Level = LogLevel.Error, Message = "{ErrorMessage} {Args}")]
    public static partial void SaveFailed(this ILogger logger, Exception exception, string errorMessage, object?[] args);

    /// <summary>Logs a blob upload failure while creating an Attachment.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationCqrsBase + 3, Level = LogLevel.Error, Message = "Error uploading blob for Attachment {FileName}")]
    public static partial void AttachmentBlobUploadFailed(this ILogger logger, Exception exception, string fileName);

    /// <summary>
    /// Logs the failed insert of an uploaded Attachment, before the D-033 re-read decides between a replay, a 409 and the
    /// save-failed result. Warning: a same-id race replay is expected, and the trace keeps the original failure even when
    /// the re-read throws.
    /// </summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationCqrsBase + 4, Level = LogLevel.Warning, Message = "Inserting the uploaded Attachment failed; the caller id re-read decides the result")]
    public static partial void AttachmentPersistAfterUploadFailed(this ILogger logger, Exception exception);
}
