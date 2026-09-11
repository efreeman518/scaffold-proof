using Azure.Messaging.ServiceBus;
using EF.Messaging.ServiceBus;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Logging;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Observability.Tracing;

namespace TaskFlow.Infrastructure.Storage;

/// <summary>
/// Service Bus implementation of the outbox transport (D-026). Registered as a singleton so the package sender
/// pool is process-wide: a ServiceBusSender is expensive to build and safe to share. The row id is the broker
/// MessageId, which is what the namespace duplicate-detection window keys on.
/// </summary>
public sealed class ServiceBusEventTransport : IIntegrationEventTransport
{
    private readonly ServiceBusSenderPool _senders;
    private readonly ILogger<ServiceBusEventTransport> _logger;

    /// <summary>Initializes the transport over the named Service Bus client.</summary>
    public ServiceBusEventTransport(
        IAzureClientFactory<ServiceBusClient> clientFactory,
        ILogger<ServiceBusEventTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        _senders = new ServiceBusSenderPool(clientFactory.CreateClient("TaskFlowSBClient"));
        _logger = logger;
    }

    /// <inheritdoc />
    public bool CanDispatch => true;

    /// <inheritdoc />
    public async Task SendBatchAsync(string destination, IReadOnlyList<OutboxMessage> messages, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) return;

        var sender = _senders.Get(destination);

        // D-047 hot path: one pooled UTF-8 buffer for the whole batch instead of a byte[] per row. Disposed
        // after the last send, because a message body is a slice of it.
        using var bodies = OutboxBodyBuffer.Rent(messages);

        var batch = await sender.CreateMessageBatchAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var row in messages)
            {
                var message = ToServiceBusMessage(row, destination, bodies);
                if (batch.TryAddMessage(message)) continue;

                if (batch.Count == 0)
                    throw new InvalidOperationException(
                        $"Outbox message {row.Id} ({row.EventType}) exceeds the Service Bus batch size limit.");

                await sender.SendMessagesAsync(batch, ct).ConfigureAwait(false);
                batch.Dispose();
                batch = await sender.CreateMessageBatchAsync(ct).ConfigureAwait(false);

                if (!batch.TryAddMessage(message))
                    throw new InvalidOperationException(
                        $"Outbox message {row.Id} ({row.EventType}) exceeds the Service Bus batch size limit.");
            }

            if (batch.Count > 0)
                await sender.SendMessagesAsync(batch, ct).ConfigureAwait(false);
        }
        finally
        {
            batch.Dispose();
        }

        _logger.OutboxBatchSent(destination, messages.Count);
    }

    /// <summary>Envelope JSON as the body; type, version and tenant as properties so a subscription rule can filter.</summary>
    private static ServiceBusMessage ToServiceBusMessage(OutboxMessage row, string destination, OutboxBodyBuffer bodies)
    {
        var message = new ServiceBusMessage(bodies.Append(row.Payload))
        {
            ContentType = "application/json",
            MessageId = row.Id.ToString(),
            Subject = row.EventType
        };

        if (row.CorrelationId is not null)
            message.CorrelationId = row.CorrelationId;

        message.ApplicationProperties["EventType"] = row.EventType;
        message.ApplicationProperties["EventVersion"] = row.EventVersion;
        message.ApplicationProperties["TenantId"] = row.TenantId.ToString();

        // D-053: one producer span per message, its trace context written into that message's own application
        // properties. Per message rather than per batch, because a batch mixes rows staged by unrelated
        // requests and a single batch span would attach every consumer to an arbitrary one.
        using var publish = MessagingTrace.StartPublish(
            MessagingTrace.ServiceBusSystem,
            destination,
            row.EventType,
            row.Id.ToString(),
            (key, value) => message.ApplicationProperties[key] = value);

        return message;
    }
}
