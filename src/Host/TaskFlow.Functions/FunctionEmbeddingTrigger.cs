using Azure.Messaging.ServiceBus;
using EF.Messaging;
using EF.Messaging.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskFlow.Application.MessageHandlers.Consumers;

namespace TaskFlow.Functions;

/// <summary>
/// pgvector embedding consumer (D-040), on its own subscription so a slow model call cannot delay projection.
/// The subscription exists only when <c>Search:Provider</c> resolves to PgVector, so on every other arm this
/// function is switched off by name with <c>AzureWebJobs.ProcessTaskEmbedding.Disabled</c> - the same
/// mechanism D-034 uses for the RabbitMq lane, and for the same reason: one deployment can flip providers.
/// </summary>
public class FunctionEmbeddingTrigger(
    ILogger<FunctionEmbeddingTrigger> logger,
    TaskEmbeddingConsumer consumer,
    IOptions<IntegrationEnvelopeReaderOptions> readerOptions)
{
    /// <summary>Generates or refreshes the embedding for a created or content-changed task.</summary>
    [Function(nameof(ProcessTaskEmbedding))]
    public Task ProcessTaskEmbedding(
        [ServiceBusTrigger("%DomainEventsTopic%", TaskEmbeddingConsumer.Name, Connection = "ServiceBus1")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken ct)
        => ServiceBusIntegrationEventDispatcher.DispatchAsync(message, actions, consumer, readerOptions.Value, logger, ct);
}
