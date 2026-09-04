using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.MessageHandlers.Consumers;

namespace TaskFlow.Functions;

/// <summary>FlowEngine workflow-start consumer, on its own subscription so a workflow outage isolates itself.</summary>
public class FunctionWorkflowTrigger(
    ILogger<FunctionWorkflowTrigger> logger,
    TaskWorkflowConsumer consumer)
{
    /// <summary>Starts the ai-task-triage workflow for newly created tasks.</summary>
    [Function(nameof(ProcessTaskWorkflowStart))]
    public Task ProcessTaskWorkflowStart(
        [ServiceBusTrigger("%DomainEventsTopic%", TaskWorkflowConsumer.Name, Connection = "ServiceBus1")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken ct)
        => ServiceBusEnvelopeReader.DispatchAsync(message, actions, consumer, logger, ct);
}
