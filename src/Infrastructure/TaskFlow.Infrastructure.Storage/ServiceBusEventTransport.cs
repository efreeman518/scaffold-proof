using Azure.Messaging.ServiceBus;
using EF.Messaging.ServiceBus;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
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
    /// <remarks>
    /// Rows are packed into broker batches in order. A row too large for an empty batch can never be sent, so it is
    /// reported permanent and skipped while packing continues: one oversize event must not fail - and eventually
    /// dead-letter - the healthy rows claimed with it. Each <c>SendMessagesAsync</c> is atomic; the first one that
    /// fails reports its own rows and every row not yet sent as transient, and earlier batches stay sent.
    /// </remarks>
    public async Task<IReadOnlyList<OutboxSendFailure>> SendBatchAsync(
        string destination, IReadOnlyList<OutboxMessage> messages, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);
        ArgumentNullException.ThrowIfNull(messages);
        if (messages.Count == 0) return [];

        var sender = _senders.Get(destination);
        var failures = new List<OutboxSendFailure>();
        // Rows in the batch being packed, with their producer spans; the spans end when that batch's send does.
        var packed = new List<(int Index, Activity? Span)>();

        // D-047 hot path: one pooled UTF-8 buffer for the whole batch instead of a byte[] per row. Disposed
        // after the last send, because a message body is a slice of it.
        using var bodies = OutboxBodyBuffer.Rent(messages);

        var batch = await sender.CreateMessageBatchAsync(ct).ConfigureAwait(false);
        try
        {
            for (var i = 0; i < messages.Count; i++)
            {
                var (message, span) = ToServiceBusMessage(messages[i], destination, bodies);
                if (batch.TryAddMessage(message))
                {
                    packed.Add((i, span));
                    continue;
                }

                if (batch.Count > 0)
                {
                    var sendError = await TrySendAsync(sender, batch, packed, ct).ConfigureAwait(false);
                    if (sendError is not null)
                    {
                        EndSpan(span, sendError.Message);
                        failures.AddRange(packed.Select(p => new OutboxSendFailure(p.Index, sendError.Message, Permanent: false)));
                        for (var rest = i; rest < messages.Count; rest++)
                            failures.Add(new OutboxSendFailure(rest, sendError.Message, Permanent: false));
                        packed.Clear();
                        _logger.OutboxBatchSendFailed(destination, messages.Count - failures.Count, failures.Count, sendError);
                        return failures;
                    }

                    packed.Clear();
                    batch.Dispose();
                    batch = await sender.CreateMessageBatchAsync(ct).ConfigureAwait(false);
                    if (batch.TryAddMessage(message))
                    {
                        packed.Add((i, span));
                        continue;
                    }
                }

                var tooLarge = $"Outbox message {messages[i].Id} ({messages[i].EventType}) exceeds the Service Bus "
                               + $"batch limit of {batch.MaxSizeInBytes} bytes.";
                EndSpan(span, tooLarge);
                failures.Add(new OutboxSendFailure(i, tooLarge, Permanent: true));
                _logger.OutboxMessageTooLarge(messages[i].Id, messages[i].EventType, destination, batch.MaxSizeInBytes);
            }

            if (batch.Count > 0)
            {
                var sendError = await TrySendAsync(sender, batch, packed, ct).ConfigureAwait(false);
                if (sendError is not null)
                {
                    failures.AddRange(packed.Select(p => new OutboxSendFailure(p.Index, sendError.Message, Permanent: false)));
                    packed.Clear();
                    _logger.OutboxBatchSendFailed(destination, messages.Count - failures.Count, failures.Count, sendError);
                    return failures;
                }

                packed.Clear();
            }
        }
        finally
        {
            // Only reached with spans still open when cancellation propagated out of a send or a batch create.
            foreach (var (_, span) in packed) span?.Dispose();
            batch.Dispose();
        }

        _logger.OutboxBatchSent(destination, messages.Count - failures.Count);
        return failures;
    }

    /// <summary>
    /// Sends one packed batch and ends its producer spans after the send completes. Returns the failure, or null
    /// on success; cancellation of <paramref name="ct"/> propagates.
    /// </summary>
    private static async Task<Exception?> TrySendAsync(
        ServiceBusSender sender, ServiceBusMessageBatch batch, List<(int Index, Activity? Span)> packed, CancellationToken ct)
    {
        try
        {
            await sender.SendMessagesAsync(batch, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            foreach (var (_, span) in packed) EndSpan(span, ex.Message);
            return ex;
        }

        foreach (var (_, span) in packed) span?.Dispose();
        return null;
    }

    private static void EndSpan(Activity? span, string error)
    {
        span?.SetStatus(ActivityStatusCode.Error, error);
        span?.Dispose();
    }

    /// <summary>
    /// Envelope JSON as the body; type, version and tenant as properties so a subscription rule can filter. The
    /// producer span is returned open: the caller ends it after the send that carries the message.
    /// </summary>
    private static (ServiceBusMessage Message, Activity? Span) ToServiceBusMessage(
        OutboxMessage row, string destination, OutboxBodyBuffer bodies)
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

        // D-053: one producer span per message, parented to the trace that staged the row and its context written
        // into that message's own application properties. Per message rather than per batch, because a batch
        // mixes rows staged by unrelated requests and a single batch span would attach every consumer to an
        // arbitrary one.
        var span = MessagingTrace.StartOutboxPublish(
            MessagingTrace.ServiceBusSystem,
            destination,
            row.EventType,
            row.Id.ToString(),
            row.TraceParent,
            row.TraceState,
            (key, value) => message.ApplicationProperties[key] = value);

        return (message, span);
    }
}
