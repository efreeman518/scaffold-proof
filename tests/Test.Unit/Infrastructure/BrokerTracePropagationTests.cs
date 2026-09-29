using EF.Messaging;
using EF.Messaging.RabbitMq;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.MessageHandlers.Consumers;
using TaskFlow.Infrastructure.Data.Interceptors;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Infrastructure.Messaging.RabbitMq;
using EF.Messaging.Tracing;
using Microsoft.Extensions.Options;
using TaskFlow.Observability.Tracing;

namespace Test.Unit.Infrastructure;

/// <summary>
/// D-053 end to end without a broker: the producer writes W3C trace context into the message it publishes, and
/// the consumer adopts that context as its parent. The parent-id assertion is the point - equal ids are what
/// makes one trace span the async hop instead of two disconnected traces. Propagation is
/// <c>EF.Messaging.Tracing.MessagingTraceContext</c>, which reads and writes the headers itself, so these
/// tests no longer depend on an OpenTelemetry SDK propagator being installed.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class BrokerTracePropagationTests
{
    private const string TraceId = "0af7651916cd43dd8448eb211c80319c";
    private const string SpanId = "b7ad6b7169203331";

    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>The published message carries a traceparent naming the producer span.</summary>
    [TestMethod]
    public void StartPublish_InjectsTraceparentNamingTheProducerSpan()
    {
        using var listener = Listen(out var started);

        var headers = new Dictionary<string, object?>(StringComparer.Ordinal);
        using (var activity = MessagingTrace.StartPublish(
            MessagingTrace.RabbitMqSystem, "taskflow.domain-events", "TaskItemCreatedEvent",
            "8f14e45f-ea4b-4b8f-9f1a-000000000001", (key, value) => headers[key] = value))
        {
            Assert.IsNotNull(activity);
            Assert.AreEqual(ActivityKind.Producer, activity.Kind);
            Assert.IsTrue(headers.TryGetValue("traceparent", out var traceparent));
            StringAssert.Contains((string?)traceparent, activity.TraceId.ToHexString());
            StringAssert.Contains((string?)traceparent, activity.SpanId.ToHexString());
        }

        var published = started.Single(a =>
            a.Kind == ActivityKind.Producer
            && (string?)a.GetTagItem("messaging.message.id") == "8f14e45f-ea4b-4b8f-9f1a-000000000001");
        Assert.AreEqual("TaskItemCreatedEvent publish", published.OperationName);
    }

    /// <summary>
    /// A delivery whose headers carry a traceparent yields a Consumer span parented to it. The header value is
    /// a UTF-8 byte array, which is how the AMQP client actually delivers a string.
    /// </summary>
    [TestMethod]
    public async Task RabbitMqHandler_WithTraceparentHeader_StartsConsumerSpanParentedToIt()
    {
        using var listener = Listen(out var started);

        var messageId = Guid.NewGuid();
        var handler = ProbeHandler();
        var result = await handler.HandleAsync(
            Delivery($"00-{TraceId}-{SpanId}-01", messageId), TestContext.CancellationToken);

        Assert.AreEqual(ConsumeOutcome.Ack, result.Outcome);
        var consumed = ConsumerSpanFor(started, messageId);
        Assert.AreEqual("process taskflow.projection", consumed.OperationName);
        Assert.AreEqual(TraceId, consumed.TraceId.ToHexString());
        Assert.AreEqual($"00-{TraceId}-{SpanId}-01", consumed.ParentId);
    }

    /// <summary>Without a traceparent the consumer still runs and simply starts its own trace.</summary>
    [TestMethod]
    public async Task RabbitMqHandler_WithoutTraceparentHeader_StartsItsOwnTrace()
    {
        using var listener = Listen(out var started);

        var messageId = Guid.NewGuid();
        var handler = ProbeHandler();
        var result = await handler.HandleAsync(
            Delivery(traceparent: null, messageId), TestContext.CancellationToken);

        Assert.AreEqual(ConsumeOutcome.Ack, result.Outcome);
        var consumed = ConsumerSpanFor(started, messageId);
        Assert.IsNull(consumed.ParentId);
    }

    /// <summary>
    /// The whole outbox hop: the request that saved the aggregate, the Scheduler drain that dispatched the row
    /// later, and the consumer. The consumer must land in the request's trace; before the row stored the trace
    /// context, it landed in the drain's (or in none).
    /// </summary>
    [TestMethod]
    public async Task OutboxHop_ConsumerContinuesTheRequestTrace_NotTheDrain()
    {
        using var listener = Listen(out var started);
        var messageId = Guid.NewGuid();
        var envelope = new IntegrationEventEnvelope(
            messageId, "TaskItemCreatedEvent", 1, DateTimeOffset.UtcNow, CorrelationId: null,
            Payload: JsonDocument.Parse($$"""{"TenantId":"{{Guid.NewGuid()}}"}""").RootElement);

        // 1. The request stages the row.
        OutboxMessage row;
        ActivityTraceId requestTrace;
        using (var request = new Activity("POST /tasks").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            requestTrace = request.TraceId;
            row = OutboxStagingInterceptor.ToRow(envelope, Guid.NewGuid(), DateTimeOffset.UtcNow);
        }

        // 2. Later, unrelated to the request, the Scheduler drain dispatches it.
        IReadOnlyDictionary<string, object?>? headers = null;
        var publisher = new Mock<IRabbitMqPublisher>();
        publisher
            .Setup(p => p.PublishBatchAsync(It.IsAny<string>(), It.IsAny<IReadOnlyList<RabbitMqMessage>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<RabbitMqMessage>, CancellationToken>((_, messages, _) => headers = messages.Single().Headers)
            .Returns(Task.CompletedTask);
        ActivityTraceId drainTrace;
        using (var drain = new Activity("OutboxMessage drain").SetIdFormat(ActivityIdFormat.W3C).Start())
        {
            drainTrace = drain.TraceId;
            await new RabbitMqEventTransport(publisher.Object).SendBatchAsync("DomainEvents", [row], TestContext.CancellationToken);
        }

        // 3. The consumer receives what was published.
        Assert.IsNotNull(headers);
        var traceparent = (string)headers["traceparent"]!;
        var result = await ProbeHandler().HandleAsync(
            Delivery(traceparent, messageId), TestContext.CancellationToken);

        Assert.AreEqual(ConsumeOutcome.Ack, result.Outcome);
        var consumed = ConsumerSpanFor(started, messageId);
        Assert.AreEqual(requestTrace, consumed.TraceId, "the consumer must continue the request that raised the event");
        Assert.AreNotEqual(drainTrace, consumed.TraceId);
    }

    // One listener per test method, but every live listener sees every activity, so tests running side by
    // side share each other's spans. Selecting by message id keeps each assertion on its own span.
    private static Activity ConsumerSpanFor(List<Activity> started, Guid messageId) =>
        started.Single(a =>
            a.Kind == ActivityKind.Consumer
            && (string?)a.GetTagItem("messaging.message.id") == messageId.ToString());

    private static ActivityListener Listen(out List<Activity> started)
    {
        var captured = new List<Activity>();
        started = captured;

        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name is TaskFlowActivitySources.MessagingName or MessagingActivitySource.Name,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = captured.Add
        };

        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    private static RabbitMqDelivery Delivery(string? traceparent, Guid messageId)
    {
        var headers = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["EventType"] = "TaskItemCreatedEvent"
        };

        if (traceparent is not null)
        {
            headers["traceparent"] = Encoding.UTF8.GetBytes(traceparent);
        }

        var envelope = new IntegrationEventEnvelope(
            messageId,
            "TaskItemCreatedEvent",
            1,
            DateTimeOffset.UtcNow,
            CorrelationId: null,
            Payload: JsonDocument.Parse($$"""{"TenantId":"{{Guid.NewGuid()}}"}""").RootElement);

        return new RabbitMqDelivery(
            Queue: "taskflow.projection",
            RoutingKey: "TaskItemCreatedEvent",
            MessageId: envelope.Id.ToString(),
            CorrelationId: null,
            ContentType: "application/json",
            Headers: headers,
            Body: JsonSerializer.SerializeToUtf8Bytes(envelope),
            DeliveryTag: 1,
            Redelivered: false,
            DeathCount: 0);
    }

    /// <summary>Claims every message once; the trace assertions do not need real inbox storage.</summary>
    private sealed class ClaimOnceInbox : IInboxStore
    {
        private readonly HashSet<(string, Guid)> _claims = [];

        public Task<InboxClaim> TryClaimAsync(string consumer, Guid messageId, TimeSpan leaseDuration, CancellationToken ct = default) =>
            Task.FromResult(_claims.Add((consumer, messageId))
                ? new InboxClaim(InboxClaimStatus.Acquired, Guid.NewGuid())
                : new InboxClaim(InboxClaimStatus.Duplicate, Guid.Empty));

        public Task<bool> CompleteAsync(string consumer, Guid messageId, Guid claimToken, CancellationToken ct = default) =>
            Task.FromResult(true);

        public Task<bool> RenewAsync(string consumer, Guid messageId, Guid claimToken, TimeSpan leaseDuration, CancellationToken ct = default)
            => Task.FromResult(true);

        public Task<bool> ReleaseAsync(string consumer, Guid messageId, Guid claimToken, CancellationToken ct = default) =>
            Task.FromResult(_claims.Remove((consumer, messageId)));

        public Task<int> PurgeAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    /// <summary>Consumer that records nothing and touches no database.</summary>
    private sealed class ProbeConsumer()
        : IntegrationEventConsumerBase(new ClaimOnceInbox(), new MessagingMetrics(), NullLogger.Instance)
    {
        public override string ConsumerName => "probe";

        public override bool Handles(string eventType) => true;

        protected override Task ConsumeAsync(IntegrationEventEnvelope envelope, CancellationToken ct) =>
            Task.CompletedTask;
    }

    /// <summary>The package delivery adapter TaskFlow registers per queue, over the probe consumer.</summary>
    private static RabbitMqIntegrationEventHandler<ProbeConsumer> ProbeHandler()
    {
        var reader = new IntegrationEnvelopeReaderOptions();
        TaskFlowIntegrationEvents.ConfigureReader(reader);
        return new(new ProbeConsumer(), Options.Create(reader), NullLogger<RabbitMqIntegrationEventHandler<ProbeConsumer>>.Instance);
    }
}
