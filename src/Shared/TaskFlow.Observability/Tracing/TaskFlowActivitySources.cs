using System.Diagnostics;

namespace TaskFlow.Observability.Tracing;

/// <summary>
/// The ActivitySources TaskFlow's own code emits from (D-053). Named once here rather than per host for the
/// same reason the meters are: a source created inside a shared library is only exported by hosts that
/// happened to register its name, so every host would have to remember. ServiceDefaults registers both.
/// </summary>
public static class TaskFlowActivitySources
{
    /// <summary>Source name for broker publish and consume spans.</summary>
    public const string MessagingName = "TaskFlow.Messaging";

    /// <summary>Source name for scheduled job and leased-drain spans.</summary>
    public const string SchedulerName = "TaskFlow.Scheduler";

    /// <summary>Publish and consume spans around the outbox and the brokers.</summary>
    public static readonly ActivitySource Messaging = new(MessagingName);

    /// <summary>Scheduled job executions and leased work-table drains.</summary>
    public static readonly ActivitySource Scheduler = new(SchedulerName);
}
