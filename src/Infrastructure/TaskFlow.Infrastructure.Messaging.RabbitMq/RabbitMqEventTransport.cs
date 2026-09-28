using EF.Messaging.RabbitMq;
using System.Diagnostics;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Observability.Tracing;

namespace TaskFlow.Infrastructure.Messaging.RabbitMq;

/// <summary>
/// RabbitMQ implementation of the outbox transport (D-034). The routing key is the event type, so the same
/// per-consumer fan-out the Service Bus correlation filters give is expressed as topic bindings. RabbitMQ has
/// no broker-side duplicate detection, so the D-029 ConsumerInbox is the only dedup on this provider. Publish
/// confirms are counted by the package's own <c>RabbitMqMetrics</c>.
/// </summary>
public sealed class RabbitMqEventTransport(IRabbitMqPublisher publisher) : IIntegrationEventTransport
{
    /// <inheritdoc />
    public bool CanDispatch => true;

    /// <inheritdoc />
    public async Task<IReadOnlyList<OutboxSendFailure>> SendBatchAsync(
        string destination, IReadOnlyList<OutboxMessage> messages, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) return [];

        // D-047 hot path: one pooled UTF-8 buffer for the whole batch instead of a byte[] per row. The
        // using scope outlives the publish, because RabbitMqMessage.Body is a slice of it.
        using var bodies = OutboxBodyBuffer.Rent(messages);

        var batch = new List<RabbitMqMessage>(messages.Count);
        var spans = new Activity?[messages.Count];
        try
        {
            for (var i = 0; i < messages.Count; i++)
            {
                var row = messages[i];
                var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["EventType"] = row.EventType,
                    ["EventVersion"] = row.EventVersion,
                    ["TenantId"] = row.TenantId.ToString(),
                    ["CorrelationId"] = row.CorrelationId
                };

                // D-053: one producer span per message, parented to the trace that staged the row, and the trace
                // context injected into that message's own headers. Per message rather than per batch because a
                // batch mixes rows staged by unrelated requests. The spans stay open until the confirms arrive.
                spans[i] = MessagingTrace.StartOutboxPublish(
                    MessagingTrace.RabbitMqSystem,
                    TaskFlowRabbitMqTopology.Exchange,
                    row.EventType,
                    row.Id.ToString(),
                    row.TraceParent,
                    row.TraceState,
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
                // Publisher confirms are awaited for the whole batch. Only the unconfirmed messages failed: the
                // rest were accepted by the broker and must be completed, not re-published with the failures.
                await publisher.PublishBatchAsync(TaskFlowRabbitMqTopology.Exchange, batch, ct).ConfigureAwait(false);
            }
            catch (RabbitMqPublishException ex)
            {
                var error = ex.InnerException is null ? ex.Message : $"{ex.Message} {ex.InnerException.Message}";
                var failures = new List<OutboxSendFailure>(ex.UnconfirmedIndices.Count);
                foreach (var index in ex.UnconfirmedIndices)
                {
                    failures.Add(new OutboxSendFailure(index, error, Permanent: false));
                    spans[index]?.SetStatus(ActivityStatusCode.Error, error);
                }

                return failures;
            }

            return [];
        }
        finally
        {
            foreach (var span in spans) span?.Dispose();
        }
    }
}
