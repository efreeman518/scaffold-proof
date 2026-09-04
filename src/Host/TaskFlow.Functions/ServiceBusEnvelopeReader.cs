using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
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

        // Transient failures propagate on purpose: settling here would lose the retry the broker owns.
        await consumer.HandleAsync(envelope!, ct);
        await actions.CompleteMessageAsync(message, cancellationToken: ct);
    }
}
