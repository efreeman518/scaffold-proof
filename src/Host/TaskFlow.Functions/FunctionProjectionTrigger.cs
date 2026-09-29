using Azure.Messaging.ServiceBus;
using EF.Messaging;
using EF.Messaging.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskFlow.Application.MessageHandlers.Consumers;

namespace TaskFlow.Functions;

/// <summary>
/// Cosmos read-model projection consumer. One function per subscription so a slow AI review cannot delay the
/// projection and each consumer gets its own delivery count and dead-letter queue.
/// </summary>
public class FunctionProjectionTrigger(
    ILogger<FunctionProjectionTrigger> logger,
    TaskProjectionConsumer consumer,
    IOptions<IntegrationEnvelopeReaderOptions> readerOptions)
{
    /// <summary>Projects task lifecycle events into the TaskView read model.</summary>
    [Function(nameof(ProcessTaskProjection))]
    public Task ProcessTaskProjection(
        [ServiceBusTrigger("%DomainEventsTopic%", TaskProjectionConsumer.Name, Connection = "ServiceBus1")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken ct)
        => ServiceBusIntegrationEventDispatcher.DispatchAsync(message, actions, consumer, readerOptions.Value, logger, ct);
}
