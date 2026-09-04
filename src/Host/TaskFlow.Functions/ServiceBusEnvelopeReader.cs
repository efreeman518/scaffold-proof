using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System.Text.Json;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.MessageHandlers.Consumers;

namespace TaskFlow.Functions;

/// <summary>
/// One reader shared by the three Service Bus triggers. It decides the three outcomes once: a message that
/// cannot be understood is dead-lettered with a reason (retrying it would fail identically forever), a message
/// the consumer handles is completed, and a transient failure is rethrown so the broker redelivers it and the
/// subscription's maxDeliveryCount eventually dead-letters it.
/// </summary>
internal static class ServiceBusEnvelopeReader
{
    /// <summary>Reason recorded on the dead-lettered message when the body is not a valid envelope.</summary>
    internal const string MalformedReason = "MalformedEnvelope";

    /// <summary>Reason recorded when the envelope is valid but this deployment has no consumer for its type.</summary>
    internal const string UnsupportedReason = "UnsupportedEventType";

    /// <summary>Reads, dispatches and settles one Service Bus delivery.</summary>
    internal static async Task DispatchAsync(
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        IntegrationEventConsumer consumer,
        ILogger logger,
        CancellationToken ct)
    {
        if (!TryRead(message, out var envelope, out var failure))
        {
            logger.EnvelopeRejected(consumer.ConsumerName, message.MessageId, failure!);
            await actions.DeadLetterMessageAsync(message, deadLetterReason: failure, cancellationToken: ct);
            return;
        }

        // Transient failures propagate on purpose: settling here would lose the retry the broker owns.
        await consumer.HandleAsync(envelope!, ct);
        await actions.CompleteMessageAsync(message, cancellationToken: ct);
    }

    /// <summary>Parses the message body as an envelope and checks the type is one this build knows.</summary>
    internal static bool TryRead(
        ServiceBusReceivedMessage message, out IntegrationEventEnvelope? envelope, out string? failure)
    {
        envelope = null;
        failure = null;

        try
        {
            envelope = JsonSerializer.Deserialize<IntegrationEventEnvelope>(message.Body.ToMemory().Span);
        }
        catch (JsonException)
        {
            failure = MalformedReason;
            return false;
        }

        if (envelope is null || string.IsNullOrEmpty(envelope.Type) || envelope.Id == Guid.Empty)
        {
            envelope = null;
            failure = MalformedReason;
            return false;
        }

        if (!IntegrationEventEnvelope.IsKnownType(envelope.Type))
        {
            envelope = null;
            failure = UnsupportedReason;
            return false;
        }

        return true;
    }
}
