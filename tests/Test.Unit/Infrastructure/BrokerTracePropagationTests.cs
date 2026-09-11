using EF.Messaging;
using EF.Messaging.RabbitMq;
using Microsoft.Extensions.Logging.Abstractions;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.MessageHandlers.Consumers;
using TaskFlow.Infrastructure.Messaging.RabbitMq;
using TaskFlow.Observability.Meters;
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
        var handler = new ProbeHandler();
        var result = await handler.HandleAsync(
            Delivery($"00-{TraceId}-{SpanId}-01", messageId), TestContext.CancellationToken);

        Assert.AreEqual(ConsumeOutcome.Ack, result.Outcome);
        var consumed = ConsumerSpanFor(started, messageId);
        Assert.AreEqual("TaskItemCreatedEvent process", consumed.OperationName);
        Assert.AreEqual(TraceId, consumed.TraceId.ToHexString());
        Assert.AreEqual($"00-{TraceId}-{SpanId}-01", consumed.ParentId);
    }

    /// <summary>Without a traceparent the consumer still runs and simply starts its own trace.</summary>
    [TestMethod]
    public async Task RabbitMqHandler_WithoutTraceparentHeader_StartsItsOwnTrace()
    {
        using var listener = Listen(out var started);

        var messageId = Guid.NewGuid();
        var handler = new ProbeHandler();
        var result = await handler.HandleAsync(
            Delivery(traceparent: null, messageId), TestContext.CancellationToken);

        Assert.AreEqual(ConsumeOutcome.Ack, result.Outcome);
        var consumed = ConsumerSpanFor(started, messageId);
        Assert.IsNull(consumed.ParentId);
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
            ShouldListenTo = source => source.Name == TaskFlowActivitySources.MessagingName,
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

        public Task<bool> TryClaimAsync(string consumer, Guid messageId, CancellationToken ct = default) =>
            Task.FromResult(_claims.Add((consumer, messageId)));

        public Task ReleaseAsync(string consumer, Guid messageId, CancellationToken ct = default)
        {
            _claims.Remove((consumer, messageId));
            return Task.CompletedTask;
        }

        public Task<int> PurgeProcessedAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default) =>
            Task.FromResult(0);
    }

    /// <summary>Consumer that records nothing and touches no database.</summary>
    private sealed class ProbeConsumer()
        : IntegrationEventConsumer(new ClaimOnceInbox(), new MessagingMetrics(), NullLogger.Instance)
    {
        public override string ConsumerName => "probe";

        public override bool Handles(string eventType) => true;

        protected override Task ConsumeAsync(IntegrationEventEnvelope envelope, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class ProbeHandler()
        : RabbitMqConsumerHandler(new ProbeConsumer(), NullLogger<ProbeHandler>.Instance);
}
