using EF.Messaging.RabbitMq;
using TaskFlow.Application.MessageHandlers.Consumers;
using TaskFlow.Domain.Shared.Events;

namespace TaskFlow.Infrastructure.Messaging.RabbitMq;

/// <summary>
/// The exchange, queues and bindings TaskFlow declares on RabbitMQ (D-034). One topic exchange keyed by event
/// type mirrors the Service Bus topic plus correlation-filtered subscriptions, so the same event reaches the
/// same three consumers on either provider.
/// </summary>
public static class TaskFlowRabbitMqTopology
{
    /// <summary>Topic exchange every integration event is published to; routing key is the event type.</summary>
    public const string Exchange = "taskflow.domain-events";

    /// <summary>Dead-letter exchange every queue routes rejected and expired messages to.</summary>
    public const string DeadLetterExchange = "taskflow.domain-events.dlx";

    /// <summary>Single queue behind the dead-letter exchange; the RabbitMQ equivalent of a Service Bus DLQ.</summary>
    public const string DeadLetterQueue = "taskflow.dead-letter";

    /// <summary>Queue drained by the Cosmos projection consumer.</summary>
    public const string ProjectionQueue = "taskflow." + TaskProjectionConsumer.Name;

    /// <summary>Queue drained by the AI review consumer.</summary>
    public const string AiReviewQueue = "taskflow." + TaskAiReviewConsumer.Name;

    /// <summary>Queue drained by the workflow-start consumer.</summary>
    public const string WorkflowQueue = "taskflow." + TaskWorkflowConsumer.Name;

    /// <summary>Builds the declaration passed to the package topology declarer. Declaration is idempotent.</summary>
    public static RabbitMqTopology Build() => new(
        Exchanges:
        [
            new RabbitMqExchange(Exchange),
            // Fanout: a dead-lettered message keeps its original routing key, which no topic binding would match.
            new RabbitMqExchange(DeadLetterExchange, Type: "fanout")
        ],
        Queues:
        [
            new RabbitMqQueue(ProjectionQueue, DeadLetterExchange: DeadLetterExchange),
            new RabbitMqQueue(AiReviewQueue, DeadLetterExchange: DeadLetterExchange),
            new RabbitMqQueue(WorkflowQueue, DeadLetterExchange: DeadLetterExchange),
            new RabbitMqQueue(DeadLetterQueue)
        ],
        Bindings:
        [
            new RabbitMqBinding(ProjectionQueue, Exchange, nameof(TaskItemCreatedEvent)),
            new RabbitMqBinding(ProjectionQueue, Exchange, nameof(TaskItemStatusChangedEvent)),
            new RabbitMqBinding(ProjectionQueue, Exchange, nameof(TaskItemCompletedEvent)),
            new RabbitMqBinding(AiReviewQueue, Exchange, nameof(TaskItemCreatedEvent)),
            new RabbitMqBinding(WorkflowQueue, Exchange, nameof(TaskItemCreatedEvent)),
            new RabbitMqBinding(DeadLetterQueue, DeadLetterExchange, "#")
        ]);
}
