using EF.Messaging.RabbitMq;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.MessageHandlers.Consumers;

namespace TaskFlow.Infrastructure.Messaging.RabbitMq;

/// <summary>
/// Adapts one delivery to the same <see cref="IntegrationEventConsumer"/> the Azure Functions triggers call
/// (D-034). The outcome mapping mirrors Service Bus settlement: an unreadable body is rejected straight to the
/// dead-letter exchange, a handled message is acknowledged, and a transient failure propagates so the package
/// requeues it until the delivery bound and then dead-letters it.
/// </summary>
/// <param name="consumer">The shared consumer this queue feeds.</param>
/// <param name="logger">Logger for rejected deliveries.</param>
public abstract class RabbitMqConsumerHandler(IntegrationEventConsumer consumer, ILogger logger) : IRabbitMqMessageHandler
{
    /// <inheritdoc />
    public async Task<ConsumeResult> HandleAsync(RabbitMqDelivery delivery, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delivery);

        if (!IntegrationEnvelopeReader.TryRead(delivery.Body.Span, out var envelope, out var failure))
        {
            logger.LogWarning(
                "Consumer {Consumer} rejected message {MessageId} from {Queue}: {Reason}",
                consumer.ConsumerName, delivery.MessageId, delivery.Queue, failure);
            return ConsumeResult.Reject(failure!);
        }

        await consumer.HandleAsync(envelope!, ct).ConfigureAwait(false);
        return ConsumeResult.Ack;
    }
}

/// <summary>Feeds <see cref="TaskProjectionConsumer"/> from the projection queue.</summary>
public sealed class RabbitMqProjectionHandler(TaskProjectionConsumer consumer, ILogger<RabbitMqProjectionHandler> logger)
    : RabbitMqConsumerHandler(consumer, logger);

/// <summary>Feeds <see cref="TaskAiReviewConsumer"/> from the ai-review queue.</summary>
public sealed class RabbitMqAiReviewHandler(TaskAiReviewConsumer consumer, ILogger<RabbitMqAiReviewHandler> logger)
    : RabbitMqConsumerHandler(consumer, logger);

/// <summary>Feeds <see cref="TaskWorkflowConsumer"/> from the workflow queue.</summary>
public sealed class RabbitMqWorkflowHandler(TaskWorkflowConsumer consumer, ILogger<RabbitMqWorkflowHandler> logger)
    : RabbitMqConsumerHandler(consumer, logger);
