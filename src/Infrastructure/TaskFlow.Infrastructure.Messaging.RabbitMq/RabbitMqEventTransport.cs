using EF.Messaging.RabbitMq;
using System.Text;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Observability.Meters;

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

        var batch = new List<RabbitMqMessage>(messages.Count);
        foreach (var row in messages)
        {
            batch.Add(new RabbitMqMessage(
                Encoding.UTF8.GetBytes(row.Payload),
                RoutingKey: row.EventType,
                MessageId: row.Id.ToString(),
                CorrelationId: row.CorrelationId,
                Headers: new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["EventType"] = row.EventType,
                    ["EventVersion"] = row.EventVersion,
                    ["TenantId"] = row.TenantId.ToString(),
                    ["CorrelationId"] = row.CorrelationId
                }));
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
