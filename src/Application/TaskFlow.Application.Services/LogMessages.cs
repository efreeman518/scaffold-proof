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
}
