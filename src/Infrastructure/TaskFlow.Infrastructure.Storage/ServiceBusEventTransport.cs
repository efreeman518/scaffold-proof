using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Data.Operational;

namespace TaskFlow.Infrastructure.Storage;

/// <summary>
/// Service Bus implementation of the outbox transport (D-026). Registered as a singleton so the sender cache is
/// process-wide: a ServiceBusSender is expensive to build and safe to share. The row id is the broker MessageId,
/// which is what the namespace duplicate-detection window keys on.
/// </summary>
public sealed class ServiceBusEventTransport : IIntegrationEventTransport, IAsyncDisposable
{
    private readonly ServiceBusClient _client;
    private readonly ILogger<ServiceBusEventTransport> _logger;
    private readonly ConcurrentDictionary<string, ServiceBusSender> _senders = new(StringComparer.Ordinal);

    /// <summary>Initializes the transport over the named Service Bus client.</summary>
    public ServiceBusEventTransport(
        IAzureClientFactory<ServiceBusClient> clientFactory,
        ILogger<ServiceBusEventTransport> logger)
    {
        ArgumentNullException.ThrowIfNull(clientFactory);
        _client = clientFactory.CreateClient("TaskFlowSBClient");
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

        var sender = _senders.GetOrAdd(destination, _client.CreateSender);

        var batch = await sender.CreateMessageBatchAsync(ct).ConfigureAwait(false);
        try
        {
            foreach (var row in messages)
            {
                var message = ToServiceBusMessage(row);
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
    private static ServiceBusMessage ToServiceBusMessage(OutboxMessage row)
    {
        var message = new ServiceBusMessage(Encoding.UTF8.GetBytes(row.Payload))
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
        return message;
    }

    /// <summary>Closes every cached sender.</summary>
    public async ValueTask DisposeAsync()
    {
        foreach (var sender in _senders.Values)
            await sender.DisposeAsync().ConfigureAwait(false);
        _senders.Clear();
    }
}
