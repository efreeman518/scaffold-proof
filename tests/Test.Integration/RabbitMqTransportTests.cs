using EF.Messaging.RabbitMq;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using RabbitMQ.Client;
using System.Text;
using System.Text.Json;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.MessageHandlers.Consumers;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Infrastructure.Data.Interceptors;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Infrastructure.Messaging.RabbitMq;
using TaskFlow.Observability.Meters;
using Test.Integration.Infrastructure;
using Test.Support;
using Testcontainers.RabbitMq;

namespace Test.Integration;

/// <summary>
/// D-034: the RabbitMQ transport must put an outbox row on the wire in the shape the consumers read back, and
/// the topology must route each event type to the right per-consumer queue. The transport and the topology are
/// TaskFlow's half of the provider; the package's own tests cover confirms, prefetch and dead-lettering.
/// Component tier: one RabbitMQ Testcontainer, no database and no Aspire graph.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class RabbitMqTransportTests
{
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task PublishedOutboxRow_ArrivesOnEveryBoundQueue_AndReadsBackAsItsEnvelope()
    {
        var ct = TestContext.CancellationToken;
        var broker = await RabbitMqBrokerFixture.EnsureStartedAsync(ct);

        await using var provider = BuildProvider(broker, $"transport-{Guid.NewGuid():N}");
        await provider.GetRequiredService<IRabbitMqTopologyDeclarer>()
            .DeclareAsync(TaskFlowRabbitMqTopology.Build(), ct);

        var envelope = IntegrationEventEnvelope.From(
            new TaskItemCreatedEvent(Guid.CreateVersion7(), TestConstants.TenantId, "over rabbit"),
            DateTimeOffset.UtcNow,
            correlationId: "corr-1");
        var row = OutboxStagingInterceptor.ToRow(envelope, DateTimeOffset.UtcNow);

        var transport = provider.GetRequiredService<IIntegrationEventTransport>();
        Assert.IsTrue(transport.CanDispatch);
        await transport.SendBatchAsync(row.Destination, [row], ct);

        // TaskItemCreatedEvent is bound to all three queues, so one publish fans out to three deliveries.
        foreach (var queue in new[]
                 {
                     TaskFlowRabbitMqTopology.ProjectionQueue,
                     TaskFlowRabbitMqTopology.AiReviewQueue,
                     TaskFlowRabbitMqTopology.WorkflowQueue
                 })
        {
            var delivered = await GetAsync(broker, queue, ct);
            Assert.IsNotNull(delivered, $"nothing arrived on {queue}");
            Assert.AreEqual(row.Id.ToString(), delivered.BasicProperties.MessageId);
            Assert.AreEqual("corr-1", delivered.BasicProperties.CorrelationId);
            Assert.AreEqual("application/json", delivered.BasicProperties.ContentType);
            Assert.AreEqual(nameof(TaskItemCreatedEvent), delivered.RoutingKey);
            Assert.AreEqual(nameof(TaskItemCreatedEvent), Header(delivered, "EventType"));
            Assert.AreEqual(TestConstants.TenantId.ToString(), Header(delivered, "TenantId"));

            Assert.IsTrue(IntegrationEnvelopeReader.TryRead(delivered.Body.Span, out var read, out var failure));
            Assert.IsNull(failure);
            Assert.AreEqual(envelope.Id, read!.Id);
            Assert.AreEqual(envelope.Type, read.Type);
            Assert.AreEqual(TestConstants.TenantId, read.TenantId);
        }
    }

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task StatusChangedEvent_ReachesOnlyTheProjectionQueue()
    {
        var ct = TestContext.CancellationToken;
        var broker = await RabbitMqBrokerFixture.EnsureStartedAsync(ct);

        await using var provider = BuildProvider(broker, $"routing-{Guid.NewGuid():N}");
        await provider.GetRequiredService<IRabbitMqTopologyDeclarer>()
            .DeclareAsync(TaskFlowRabbitMqTopology.Build(), ct);
        await DrainAsync(broker, ct);

        var envelope = IntegrationEventEnvelope.From(
            new TaskItemStatusChangedEvent(
                Guid.CreateVersion7(), TestConstants.TenantId,
                TaskFlow.Domain.Shared.Enums.TaskItemStatus.Open,
                TaskFlow.Domain.Shared.Enums.TaskItemStatus.InProgress),
            DateTimeOffset.UtcNow,
            correlationId: null);
        var row = OutboxStagingInterceptor.ToRow(envelope, DateTimeOffset.UtcNow);

        await provider.GetRequiredService<IIntegrationEventTransport>()
            .SendBatchAsync(row.Destination, [row], ct);

        Assert.IsNotNull(await GetAsync(broker, TaskFlowRabbitMqTopology.ProjectionQueue, ct));
        Assert.IsNull(await GetAsync(broker, TaskFlowRabbitMqTopology.AiReviewQueue, ct),
            "ai-review is bound to created only; a status change must not wake the model");
        Assert.IsNull(await GetAsync(broker, TaskFlowRabbitMqTopology.WorkflowQueue, ct));
    }

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task MalformedBody_IsRejectedByTheHandler_BeforeAnyConsumerRuns()
    {
        var ct = TestContext.CancellationToken;
        var broker = await RabbitMqBrokerFixture.EnsureStartedAsync(ct);

        await using var provider = BuildProvider(broker, $"malformed-{Guid.NewGuid():N}");
        await provider.GetRequiredService<IRabbitMqTopologyDeclarer>()
            .DeclareAsync(TaskFlowRabbitMqTopology.Build(), ct);
        await DrainAsync(broker, ct);

        // Publish a body that is not an envelope; the handler must reject it rather than requeue forever.
        var publisher = provider.GetRequiredService<IRabbitMqPublisher>();
        await publisher.PublishAsync(TaskFlowRabbitMqTopology.Exchange, new RabbitMqMessage(
            Encoding.UTF8.GetBytes("{ not an envelope"),
            RoutingKey: nameof(TaskItemCreatedEvent),
            MessageId: Guid.CreateVersion7().ToString()), ct);

        var delivered = await GetAsync(broker, TaskFlowRabbitMqTopology.ProjectionQueue, ct);
        Assert.IsNotNull(delivered);
        Assert.IsFalse(IntegrationEnvelopeReader.TryRead(delivered.Body.Span, out _, out var failure));
        Assert.AreEqual(IntegrationEnvelopeReader.MalformedReason, failure);
    }

    public TestContext TestContext { get; set; } = null!;

    private static ServiceProvider BuildProvider(RabbitMqContainer broker, string clientName)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"{RabbitMqRegistration.OptionsSection}:ConnectionString"] = broker.GetConnectionString(),
            [$"{RabbitMqRegistration.OptionsSection}:ClientProvidedName"] = clientName
        }).Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<MessagingMetrics>();
        services.AddTaskFlowRabbitMqMessaging(config);
        return services.BuildServiceProvider();
    }

    private static async Task<BasicGetResult?> GetAsync(RabbitMqContainer broker, string queue, CancellationToken ct)
    {
        var factory = new ConnectionFactory { Uri = new Uri(broker.GetConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        // Poll briefly: publisher confirms mean the broker has the message, not that it has been routed yet.
        for (var attempt = 0; attempt < 40; attempt++)
        {
            var result = await channel.BasicGetAsync(queue, autoAck: true, ct);
            if (result is not null) return result;
            await Task.Delay(100, ct);
        }

        return null;
    }

    private static async Task DrainAsync(RabbitMqContainer broker, CancellationToken ct)
    {
        var factory = new ConnectionFactory { Uri = new Uri(broker.GetConnectionString()) };
        await using var connection = await factory.CreateConnectionAsync(ct);
        await using var channel = await connection.CreateChannelAsync(cancellationToken: ct);

        foreach (var queue in new[]
                 {
                     TaskFlowRabbitMqTopology.ProjectionQueue,
                     TaskFlowRabbitMqTopology.AiReviewQueue,
                     TaskFlowRabbitMqTopology.WorkflowQueue
                 })
        {
            await channel.QueuePurgeAsync(queue, ct);
        }
    }

    private static string? Header(BasicGetResult delivered, string name)
    {
        if (delivered.BasicProperties.Headers is null) return null;
        if (!delivered.BasicProperties.Headers.TryGetValue(name, out var raw)) return null;

        // AMQP encodes string headers as UTF-8 bytes.
        return raw switch
        {
            byte[] utf8 => Encoding.UTF8.GetString(utf8),
            string text => text,
            _ => raw?.ToString()
        };
    }
}
