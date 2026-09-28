using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.MessageHandlers.Consumers;
using TaskFlow.Observability.Tracing;

namespace TaskFlow.Functions;

/// <summary>
/// One reader shared by the three Service Bus triggers. It decides the three outcomes once: a message that
/// cannot be understood is dead-lettered with a reason (retrying it would fail identically forever), a message
/// the consumer handles is completed, and a transient failure is rethrown so the broker redelivers it and the
/// subscription's maxDeliveryCount eventually dead-letters it.
/// </summary>
internal static class ServiceBusEnvelopeReader
{
    /// <summary>Reads, dispatches and settles one Service Bus delivery.</summary>
    internal static async Task DispatchAsync(
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        IntegrationEventConsumer consumer,
        ILogger logger,
        CancellationToken ct)
    {
        if (!IntegrationEnvelopeReader.TryRead(message.Body.ToMemory().Span, out var envelope, out var failure))
        {
            logger.EnvelopeRejected(consumer.ConsumerName, message.MessageId, failure!);
            await actions.DeadLetterMessageAsync(message, deadLetterReason: failure, cancellationToken: ct);
            return;
        }

        // D-053: the consume span continues the producer's trace. Started after the body is readable so a
        // dead-lettered message stays a logged rejection rather than a span reporting a parse failure.
        using var process = MessagingTrace.StartProcess(
            MessagingTrace.ServiceBusSystem,
            consumer.ConsumerName,
            envelope!.Type,
            message.MessageId,
            key => message.ApplicationProperties.TryGetValue(key, out var value) ? value?.ToString() : null);

        // Transient failures propagate on purpose: settling here would lose the retry the broker owns. That
        // includes InboxClaimInProgressException - another delivery holds a live claim - which the runtime
        // abandons so the message comes back once that delivery has completed or its lease has expired.
        await consumer.HandleAsync(envelope, ct);

        // Not the invocation token: the effect has run and the inbox claim is completed, so a shutdown now must
        // not turn a finished message into a redelivery.
        await actions.CompleteMessageAsync(message, CancellationToken.None);
    }
}
