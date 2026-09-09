using EF.Messaging.RabbitMq;
using Microsoft.Extensions.Logging;
using System.Text;
using TaskFlow.Application.MessageHandlers.Consumers;
using TaskFlow.Observability.Tracing;

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

        // D-053: the consume span continues the producer's trace. Started after the body is readable so an
        // unreadable message stays a rejection with its reason logged, not a span reporting a parse failure.
        using var process = MessagingTrace.StartProcess(
            MessagingTrace.RabbitMqSystem,
            delivery.Queue,
            envelope!.Type,
            delivery.MessageId,
            key => HeaderText(delivery.Headers, key));

        await consumer.HandleAsync(envelope, ct).ConfigureAwait(false);
        return ConsumeResult.Ack;
    }

    /// <summary>
    /// Reads one header as text. The AMQP field table carries strings as UTF-8 byte arrays, so a plain cast to
    /// string returns null for a header that is present - which would silently drop the trace context.
    /// </summary>
    private static string? HeaderText(IReadOnlyDictionary<string, object?> headers, string key) =>
        headers.TryGetValue(key, out var value)
            ? value switch
            {
                string text => text,
                byte[] utf8 => Encoding.UTF8.GetString(utf8),
                ReadOnlyMemory<byte> utf8 => Encoding.UTF8.GetString(utf8.Span),
                _ => value?.ToString()
            }
            : null;
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

/// <summary>Feeds <see cref="TaskEmbeddingConsumer"/> from the embedding queue; PgVector arm only (D-040).</summary>
public sealed class RabbitMqEmbeddingHandler(TaskEmbeddingConsumer consumer, ILogger<RabbitMqEmbeddingHandler> logger)
    : RabbitMqConsumerHandler(consumer, logger);
