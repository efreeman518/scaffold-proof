namespace TaskFlow.Application.Contracts.Services;

/// <summary>
/// D6 - Asynchronous, event-driven inference. Invoked by the ai-review consumer after a task is created: the
/// model reviews the new task and posts clarifying questions or missing-detail notes as a comment. The port
/// lives here so the consumer (Application) can call it whichever transport delivered the event (D-034).
/// </summary>
public interface IAiTaskReviewer
{
    /// <summary>Reviews a newly created task and, if it is not already clear, posts a comment.</summary>
    Task ReviewNewTaskAsync(Guid taskId, Guid tenantId, CancellationToken ct = default);
}
