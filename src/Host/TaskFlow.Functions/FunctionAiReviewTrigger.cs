using Azure.Messaging.ServiceBus;
using EF.Messaging;
using EF.Messaging.Functions;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TaskFlow.Application.MessageHandlers.Consumers;

namespace TaskFlow.Functions;

/// <summary>D6 AI readiness review consumer, on its own subscription so model latency stays off the projection path.</summary>
public class FunctionAiReviewTrigger(
    ILogger<FunctionAiReviewTrigger> logger,
    TaskAiReviewConsumer consumer,
    IOptions<IntegrationEnvelopeReaderOptions> readerOptions)
{
    /// <summary>Reviews newly created tasks and posts clarifying questions as a comment.</summary>
    [Function(nameof(ProcessTaskAiReview))]
    public Task ProcessTaskAiReview(
        [ServiceBusTrigger("%DomainEventsTopic%", TaskAiReviewConsumer.Name, Connection = "ServiceBus1")]
        ServiceBusReceivedMessage message,
        ServiceBusMessageActions actions,
        CancellationToken ct)
        => ServiceBusIntegrationEventDispatcher.DispatchAsync(message, actions, consumer, readerOptions.Value, logger, ct);
}
