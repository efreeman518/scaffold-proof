namespace TaskFlow.Application.Contracts.Services;

/// <summary>Counter adjustments applied to an existing TaskView document without re-reading the aggregate.</summary>
/// <param name="CommentCount">Change to the comment counter.</param>
/// <param name="AttachmentCount">Change to the attachment counter.</param>
/// <param name="ChecklistTotal">Change to the checklist item count.</param>
/// <param name="ChecklistCompleted">Change to the completed checklist item count.</param>
public readonly record struct TaskViewCounterDelta(
    int CommentCount = 0,
    int AttachmentCount = 0,
    int ChecklistTotal = 0,
    int ChecklistCompleted = 0);

/// <summary>Coordinates i task view projection application use cases with validation, tenant checks, repositories, and response shaping.</summary>
public interface ITaskViewProjectionService
{
    /// <summary>
    /// Rebuilds the whole TaskView document from the authoritative store. Used for create and status change,
    /// where the projected shape really does change.
    /// </summary>
    /// <param name="taskItemId">Task to project.</param>
    /// <param name="occurredAtUtc">Event time; becomes LastModifiedUtc and guards against out-of-order redelivery.</param>
    /// <param name="ct">Cancellation token.</param>
    Task ProjectTaskItemAsync(Guid taskItemId, DateTimeOffset occurredAtUtc, CancellationToken ct = default);

    /// <summary>Applies counter deltas in place for events that change nothing else about the projection.</summary>
    /// <param name="taskItemId">Task whose document is patched.</param>
    /// <param name="tenantId">Partition key of the document.</param>
    /// <param name="delta">Counter changes to apply.</param>
    /// <param name="occurredAtUtc">Event time written as LastModifiedUtc.</param>
    /// <param name="ct">Cancellation token.</param>
    Task AdjustCountersAsync(
        Guid taskItemId, Guid tenantId, TaskViewCounterDelta delta, DateTimeOffset occurredAtUtc, CancellationToken ct = default);
}
