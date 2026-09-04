using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Model;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TaskFlow.Domain.Shared.Events;

namespace TaskFlow.Application.MessageHandlers;

// Standalone trigger that maps TaskFlow integration events to FlowEngine workflow starts.
//
// Not wired to the InternalMessageBus (TaskItem events aren't IMessage and travel
// out over Service Bus, not in-process). Callers invoke methods directly - typically
// from TaskItemService right after eventPublisher.PublishAsync, or from a custom
// Service Bus subscriber in TaskFlow.Functions. For the demo, manual triggering via
// the dashboard /workflows/run page is sufficient; this class makes domain-event
// triggering a one-line addition wherever the event is raised.
public interface IWorkflowTrigger
{
    /// <summary>Handles task item created events for workflow trigger.</summary>
    Task OnTaskItemCreatedAsync(TaskItemCreatedEvent evt, CancellationToken ct = default);
}

/// <summary>Handles workflow trigger work by coordinating validation, tenant boundaries, persistence, and response mapping.</summary>
public sealed class WorkflowTriggerHandler(
    IFlowEngine engine,
    ILogger<WorkflowTriggerHandler> logger) : IWorkflowTrigger
{
    /// <summary>Handles task item created events for workflow trigger handler.</summary>
    public async Task OnTaskItemCreatedAsync(TaskItemCreatedEvent evt, CancellationToken ct = default)
    {
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
        };

        try
        {
            var instance = await engine.StartBackgroundAsync(request, ct);
            logger.WorkflowStarted(request.WorkflowId, instance.InstanceId, evt.TaskItemId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to start ai-task-triage for TaskItem {TaskId}", evt.TaskItemId);
        }
    }

    /// <summary>Wraps asynchronous work for workflow trigger handler with shared logging and error handling.</summary>
    private static JsonContextValue Wrap(string value)
        => new() { Value = JsonSerializer.SerializeToElement(value) };
}
