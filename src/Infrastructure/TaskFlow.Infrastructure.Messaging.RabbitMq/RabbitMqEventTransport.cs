using EF.Messaging.RabbitMq;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Observability.Meters;
using TaskFlow.Observability.Tracing;

namespace TaskFlow.Infrastructure.Messaging.RabbitMq;

/// <summary>
/// RabbitMQ implementation of the outbox transport (D-034). The routing key is the event type, so the same
/// per-consumer fan-out the Service Bus correlation filters give is expressed as topic bindings. RabbitMQ has
/// no broker-side duplicate detection, so the D-029 ConsumerInbox is the only dedup on this provider.
/// </summary>
public sealed class RabbitMqEventTransport(
    IRabbitMqPublisher publisher,
    MessagingMetrics metrics) : IIntegrationEventTransport
{
    /// <inheritdoc />
    public bool CanDispatch => true;

    /// <inheritdoc />
    public async Task SendBatchAsync(string destination, IReadOnlyList<OutboxMessage> messages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) return;

        // D-047 hot path: one pooled UTF-8 buffer for the whole batch instead of a byte[] per row. The
        // using scope outlives the publish, because RabbitMqMessage.Body is a slice of it.
        using var bodies = OutboxBodyBuffer.Rent(messages);

        var batch = new List<RabbitMqMessage>(messages.Count);
        foreach (var row in messages)
        {
            var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["EventType"] = row.EventType,
                ["EventVersion"] = row.EventVersion,
                ["TenantId"] = row.TenantId.ToString(),
                ["CorrelationId"] = row.CorrelationId
            };

            // D-053: one producer span per message, and the trace context injected into that message's own
            // headers. Per message rather than per batch because a batch mixes rows staged by unrelated
            // requests, so a single span for the batch would attach every consumer to an arbitrary one.
            using var publish = MessagingTrace.StartPublish(
                MessagingTrace.RabbitMqSystem,
                TaskFlowRabbitMqTopology.Exchange,
                row.EventType,
                row.Id.ToString(),
                (key, value) => headers[key] = value);

            batch.Add(new RabbitMqMessage(
                bodies.Append(row.Payload),
                RoutingKey: row.EventType,
                MessageId: row.Id.ToString(),
                CorrelationId: row.CorrelationId,
                Headers: headers));
        }

        try
        {
            // Publisher confirms are awaited for the whole batch; an unconfirmed message throws, which
            // propagates to the dispatcher so it releases the lease and the rows are retried.
            await publisher.PublishBatchAsync(TaskFlowRabbitMqTopology.Exchange, batch, ct).ConfigureAwait(false);
        }
        catch (RabbitMqPublishException ex)
        {
            metrics.RecordRabbitPublish(batch.Count - ex.UnconfirmedIndices.Count, ex.UnconfirmedIndices.Count);
            throw;
        }

        metrics.RecordRabbitPublish(batch.Count, 0);
    }
}
