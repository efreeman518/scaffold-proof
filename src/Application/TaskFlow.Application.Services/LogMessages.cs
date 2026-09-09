using Microsoft.Extensions.Logging;
using TaskFlow.Observability;

namespace TaskFlow.Application.Services;

/// <summary>
/// Source-generated logging methods for the Application.Services layer. Using
/// <see cref="LoggerMessageAttribute"/> defers argument evaluation until the log level is enabled,
/// satisfying CA1873 and avoiding needless work.
/// </summary>
internal static partial class LogMessages
{
    /// <summary>Logs that a TaskItem was projected to a TaskView read model.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 1, Level = LogLevel.Information, Message = "Projected TaskItem {Id} to TaskView")]
    public static partial void TaskItemProjected(this ILogger logger, Guid id);

    /// <summary>Logs that an out-of-order projection event was skipped.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 20, Level = LogLevel.Debug, Message = "TaskView {Id} projection skipped: event {OccurredAtUtc} is older than projection {LastModifiedUtc}")]
    public static partial void TaskViewProjectionSkipped(this ILogger logger, Guid id, DateTimeOffset occurredAtUtc, DateTimeOffset lastModifiedUtc);

    /// <summary>Logs that a TaskItem search was cancelled by the client.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 21, Level = LogLevel.Debug, Message = "TaskItem search cancelled by client.")]
    public static partial void TaskItemSearchCancelled(this ILogger logger);

    /// <summary>Logs a Category create failure.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 22, Level = LogLevel.Error, Message = "Error creating Category")]
    public static partial void CategoryCreateFailed(this ILogger logger, Exception exception);

    /// <summary>Logs a Category update failure.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 23, Level = LogLevel.Error, Message = "Error updating Category {Id}")]
    public static partial void CategoryUpdateFailed(this ILogger logger, Exception exception, Guid? id);

    /// <summary>Logs a Category delete failure.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 24, Level = LogLevel.Error, Message = "Error deleting Category {Id}")]
    public static partial void CategoryDeleteFailed(this ILogger logger, Exception exception, Guid id);

    /// <summary>Logs an Attachment create failure.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 25, Level = LogLevel.Error, Message = "Error creating Attachment")]
    public static partial void AttachmentCreateFailed(this ILogger logger, Exception exception);

    /// <summary>Logs a blob upload failure while creating an Attachment.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 26, Level = LogLevel.Error, Message = "Error uploading blob for Attachment {FileName}")]
    public static partial void AttachmentBlobUploadFailed(this ILogger logger, Exception exception, string fileName);

    /// <summary>Logs a persistence failure after an Attachment blob upload succeeded.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 27, Level = LogLevel.Error, Message = "Error persisting Attachment after upload")]
    public static partial void AttachmentPersistAfterUploadFailed(this ILogger logger, Exception exception);

    /// <summary>Logs an Attachment update failure.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 28, Level = LogLevel.Error, Message = "Error updating Attachment {Id}")]
    public static partial void AttachmentUpdateFailed(this ILogger logger, Exception exception, Guid? id);

    /// <summary>Logs an Attachment delete failure.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 29, Level = LogLevel.Error, Message = "Error deleting Attachment {Id}")]
    public static partial void AttachmentDeleteFailed(this ILogger logger, Exception exception, Guid id);

    /// <summary>Logs a blob delete failure after an Attachment was deleted.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 30, Level = LogLevel.Warning, Message = "Failed to delete blob for Attachment {Id}")]
    public static partial void AttachmentBlobDeleteFailed(this ILogger logger, Exception exception, Guid id);

    /// <summary>Logs a Tag create failure.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 31, Level = LogLevel.Error, Message = "Error creating Tag")]
    public static partial void TagCreateFailed(this ILogger logger, Exception exception);

    /// <summary>Logs a Tag update failure.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 32, Level = LogLevel.Error, Message = "Error updating Tag {Id}")]
    public static partial void TagUpdateFailed(this ILogger logger, Exception exception, Guid? id);

    /// <summary>Logs a Tag delete failure.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 33, Level = LogLevel.Error, Message = "Error deleting Tag {Id}")]
    public static partial void TagDeleteFailed(this ILogger logger, Exception exception, Guid id);

    /// <summary>Logs a TaskItem create failure.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 34, Level = LogLevel.Error, Message = "Error creating TaskItem")]
    public static partial void TaskItemCreateFailed(this ILogger logger, Exception exception);

    /// <summary>Logs a TaskItem update failure.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 35, Level = LogLevel.Error, Message = "Error updating TaskItem {Id}")]
    public static partial void TaskItemUpdateFailed(this ILogger logger, Exception exception, Guid? id);

    /// <summary>Logs a TaskItem patch failure.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 36, Level = LogLevel.Error, Message = "Error patching TaskItem {Id}")]
    public static partial void TaskItemPatchFailed(this ILogger logger, Exception exception, Guid id);

    /// <summary>Logs a TaskItem delete failure.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 37, Level = LogLevel.Error, Message = "Error deleting TaskItem {Id}")]
    public static partial void TaskItemDeleteFailed(this ILogger logger, Exception exception, Guid id);

    /// <summary>
    /// Logs a save failure from the nested-children aggregate save helper, carrying the caller-supplied
    /// error message and args verbatim (the message template itself is fixed; only the values vary per call site).
    /// </summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 38, Level = LogLevel.Error, Message = "{ErrorMessage} {Args}")]
    public static partial void AggregateSaveFailed(this ILogger logger, Exception exception, string errorMessage, object?[] args);

    /// <summary>Logs that a TaskItem could not be found for read-model projection.</summary>
    [LoggerMessage(EventId = LogEventIds.ApplicationServicesBase + 39, Level = LogLevel.Warning, Message = "TaskItem {Id} not found for projection")]
    public static partial void TaskViewNotFoundForProjection(this ILogger logger, Guid id);
}
