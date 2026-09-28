namespace TaskFlow.Scheduler.Workers;

/// <summary>What a work-table handler reported for one row of a claimed batch.</summary>
/// <param name="IsCompleted">True when the row was handled and is hard-deleted.</param>
/// <param name="Error">Failure reason; null when completed.</param>
/// <param name="Permanent">True when retrying cannot succeed and the row is dead-lettered at once.</param>
public sealed record WorkItemOutcome(bool IsCompleted, string? Error, bool Permanent)
{
    /// <summary>A handled row.</summary>
    public static WorkItemOutcome Completed { get; } = new(true, null, false);

    /// <summary>A failed row.</summary>
    public static WorkItemOutcome Failed(string error, bool permanent) => new(false, error, permanent);
}
