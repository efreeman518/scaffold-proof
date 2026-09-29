using System.Diagnostics;

namespace TaskFlow.Observability.Tracing;

/// <summary>
/// The ActivitySource TaskFlow's own code emits from (D-053). Named once here rather than per host for the
/// same reason the meters are: a source created inside a shared library is only exported by hosts that
/// happened to register its name, so every host would have to remember. ServiceDefaults registers it, next to
/// the package sources that now own the broker and outbox spans (<c>EF.Messaging</c>, <c>EF.Data.Outbox</c>).
/// </summary>
public static class TaskFlowActivitySources
{
    /// <summary>Source name for scheduled job spans.</summary>
    public const string SchedulerName = "TaskFlow.Scheduler";

    /// <summary>Scheduled job executions.</summary>
    public static readonly ActivitySource Scheduler = new(SchedulerName);
}
