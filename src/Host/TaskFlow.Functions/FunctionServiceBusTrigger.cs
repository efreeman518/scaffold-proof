using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Application.Contracts.Services;
using TaskFlow.Application.MessageHandlers;
using TaskFlow.Infrastructure.AI.Demos;

namespace TaskFlow.Functions;

/// <summary>
/// Service Bus projection trigger. It consumes application integration events from the
/// DomainEvents topic and updates read models through application services. On task creation it
/// also runs the event-driven AI readiness review (D6) when a Foundry model is wired.
/// </summary>
public class FunctionServiceBusTrigger(
    ILogger<FunctionServiceBusTrigger> logger,
    ITaskViewProjectionService projectionService,
    IAiTaskReviewer aiTaskReviewer,
    IWorkflowTrigger workflowTrigger)
{
    /// <summary>
    /// Dispatches task-related events by message Subject. Unknown event types are logged because
    /// the topic can contain future integration events that this Function version does not handle.
    /// </summary>
    [Function(nameof(ProcessTaskEvent))]
    public async Task ProcessTaskEvent(
        [ServiceBusTrigger("%DomainEventsTopic%", "%DomainEventsSubscription%",
            Connection = "ServiceBus1", IsSessionsEnabled = false)]
        string messageBody,
        FunctionContext context,
        CancellationToken ct)
    {
        var eventType = "Unknown";
        if (context.BindingContext.BindingData.TryGetValue("Subject", out var subjectObj))
            eventType = subjectObj?.ToString() ?? eventType;

        logger.ServiceBusReceived(eventType, messageBody.Length);

        switch (eventType)
        {
            case "TaskItemCreatedEvent":
            case "TaskItemStatusChangedEvent":
                var taskItemId = ExtractTaskItemId(messageBody);
                if (taskItemId.HasValue)
                    await projectionService.ProjectTaskItemAsync(taskItemId.Value, ct);

                // D6: event-driven AI readiness review on creation (no-op when no model is wired).
                if (eventType == "TaskItemCreatedEvent" && taskItemId.HasValue)
                {
                    var tenantId = ExtractTenantId(messageBody);
                    await aiTaskReviewer.ReviewNewTaskAsync(taskItemId.Value, tenantId, ct);
                    await workflowTrigger.OnTaskItemCreatedAsync(
                        new TaskItemCreatedEvent(taskItemId.Value, tenantId, ExtractTitle(messageBody)),
                        ct);
                }
                break;
            default:
                logger.LogWarning("Unknown event type: {EventType}", eventType);
                break;
        }
    }

    /// <summary>Extracts task item ID from the supplied message or payload.</summary>
    private static Guid? ExtractTaskItemId(string messageBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(messageBody);
            if (doc.RootElement.TryGetProperty("TaskItemId", out var prop))
                return prop.GetGuid();
        }
        catch { }
        return null;
    }

    /// <summary>Extracts the tenant ID from the event payload, defaulting to empty when absent.</summary>
    private static Guid ExtractTenantId(string messageBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(messageBody);
            if (doc.RootElement.TryGetProperty("TenantId", out var prop) && prop.TryGetGuid(out var tenantId))
                return tenantId;
        }
        catch { }
        return Guid.Empty;
    }

    /// <summary>Extracts the task title from the event payload for workflow triage context.</summary>
    private static string ExtractTitle(string messageBody)
    {
        try
        {
            using var doc = JsonDocument.Parse(messageBody);
            if (doc.RootElement.TryGetProperty("Title", out var prop))
                return prop.GetString() ?? string.Empty;
        }
        catch { }
        return string.Empty;
    }
}
