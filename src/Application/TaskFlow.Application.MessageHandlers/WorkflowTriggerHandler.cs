using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Model;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TaskFlow.Domain.Shared.Events;

namespace TaskFlow.Application.MessageHandlers;

// Maps TaskFlow integration events to FlowEngine workflow starts.
//
// Not wired to the InternalMessageBus (TaskItem events are not IMessage and travel out over the broker, not
// in-process). The workflow consumer calls this after claiming the message in the D-029 inbox; the dashboard
// /workflows/run page still triggers it manually.
public interface IWorkflowTrigger
{
    /// <summary>Handles task item created events for workflow trigger.</summary>
    /// <param name="evt">The created event payload.</param>
    /// <param name="idempotencyKey">Stable key so a redelivery does not start a second instance (D-029).</param>
    /// <param name="ct">Cancellation token.</param>
    Task OnTaskItemCreatedAsync(TaskItemCreatedEvent evt, string idempotencyKey, CancellationToken ct = default);
}

/// <summary>Handles workflow trigger work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
public sealed class WorkflowTriggerHandler(
    IFlowEngine engine,
    ILogger<WorkflowTriggerHandler> logger) : IWorkflowTrigger
{
    /// <summary>Handles task item created events for workflow trigger handler.</summary>
    public async Task OnTaskItemCreatedAsync(TaskItemCreatedEvent evt, string idempotencyKey, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(evt);
        var request = new StartRequest
        {
            WorkflowId = "ai-task-triage",
            Entity = new JsonContextValue { Value = JsonSerializer.SerializeToElement(evt) },
            Params = new Dictionary<string, ContextValue>
            {
                ["tenantId"] = Wrap(evt.TenantId.ToString()),
                ["taskId"] = Wrap(evt.TaskItemId.ToString()),
                ["description"] = Wrap(evt.Title ?? string.Empty),
            },
            CorrelationId = evt.TaskItemId.ToString(),
            TenantId = evt.TenantId.ToString(),
            IdempotencyKey = idempotencyKey,
        };

        // Deliberately not caught: a failed start must release the inbox claim so the broker redelivers.
        var instance = await engine.StartBackgroundAsync(request, ct);
        logger.WorkflowStarted(request.WorkflowId, instance.InstanceId, evt.TaskItemId);
    }

    /// <summary>Wraps asynchronous work for workflow trigger handler with shared logging and error handling.</summary>
    private static JsonContextValue Wrap(string value)
        => new() { Value = JsonSerializer.SerializeToElement(value) };
}
