using Azure.Messaging.ServiceBus;
using EF.Messaging.RabbitMq;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using System.Diagnostics;
using TaskFlow.Infrastructure.Data.Messaging;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Infrastructure.Messaging.RabbitMq;
using TaskFlow.Infrastructure.Storage;
using TaskFlow.Observability.Tracing;

namespace Test.Unit.Infrastructure;

/// <summary>
/// Per-message results and producer spans of the two outbox transports (D-026, D-053). A transport that reports
/// the whole group for one failure makes the dispatcher re-publish messages the broker already accepted, or
/// dead-letter healthy rows that were only claimed next to an oversize one; a producer span that ends before the
/// send, or hangs off the Scheduler drain, breaks the trace the outbox is supposed to continue.
/// Pure-unit tier: mocked broker clients, no network.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class OutboxTransportTests
{
    private const string StoredTraceId = "4bf92f3577b34da6a3ce929d0e0e4736";
    private const string StoredSpanId = "00f067aa0ba902b7";
    private const string StoredTraceParent = $"00-{StoredTraceId}-{StoredSpanId}-01";

    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Only the unconfirmed messages fail; every confirmed one is reported as accepted.</summary>
    [TestMethod]
    public async Task RabbitMq_PartiallyConfirmedBatch_ReportsOnlyTheUnconfirmedIndices()
    {
        var publisher = new Mock<IRabbitMqPublisher>(MockBehavior.Strict);
        publisher
            .Setup(p => p.PublishBatchAsync(TaskFlowRabbitMqTopology.Exchange, It.IsAny<IReadOnlyList<RabbitMqMessage>>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new RabbitMqPublishException("2 of 5 not confirmed", [1, 3], new TimeoutException("confirm timeout")));
        var transport = new RabbitMqEventTransport(publisher.Object);

        var failures = await transport.SendBatchAsync("DomainEvents", Rows(5), TestContext.CancellationToken);

        CollectionAssert.AreEqual(new[] { 1, 3 }, failures.Select(f => f.Index).ToList());
        Assert.IsTrue(failures.All(f => !f.Permanent), "an unconfirmed publish is retried, not parked");
        StringAssert.Contains(failures[0].Error, "confirm timeout");
    }

    /// <summary>
    /// The producer span is parented to the context stored at staging, links the drain span that carried it, and
    /// is still open while the broker confirms.
    /// </summary>
    [TestMethod]
    public async Task RabbitMq_ProducerSpan_ContinuesTheStoredTraceAndCoversThePublish()
    {
        using var listener = Listen(out var started);
        var rows = Rows(1, traceParent: StoredTraceParent);
        var publisher = new Mock<IRabbitMqPublisher>(MockBehavior.Strict);
        RabbitMqMessage? published = null;
        Activity? spanAtPublish = null;
        publisher
            .Setup(p => p.PublishBatchAsync(TaskFlowRabbitMqTopology.Exchange, It.IsAny<IReadOnlyList<RabbitMqMessage>>(), It.IsAny<CancellationToken>()))
            .Callback<string, IReadOnlyList<RabbitMqMessage>, CancellationToken>((_, messages, _) =>
            {
                published = messages.Single();
                spanAtPublish = ProducerFor(started, rows[0].Id);
                Assert.IsFalse(spanAtPublish.IsStopped, "the producer span ended before the publish");
            })
            .Returns(Task.CompletedTask);
        var transport = new RabbitMqEventTransport(publisher.Object);

        using var drain = new Activity("OutboxMessage drain").SetIdFormat(ActivityIdFormat.W3C).Start();
        var failures = await transport.SendBatchAsync("DomainEvents", rows, TestContext.CancellationToken);

        Assert.IsEmpty(failures);
        Assert.IsNotNull(spanAtPublish);
        AssertContinuesStoredTrace(spanAtPublish, drain);
        Assert.IsTrue(spanAtPublish.IsStopped, "the producer span must end once the confirm arrived");
        Assert.IsNotNull(published?.Headers);
        Assert.AreEqual($"00-{StoredTraceId}-{spanAtPublish.SpanId.ToHexString()}-01", published.Headers["traceparent"]);
    }

    /// <summary>
    /// A row that can never fit a Service Bus batch is reported permanent on its own; the rows around it are
    /// still packed and sent.
    /// </summary>
    [TestMethod]
    public async Task ServiceBus_OversizeRow_IsPermanentAlone_AndTheRestAreSent()
    {
        var rows = Rows(3);
        rows[1].Payload = new string('x', 5000);
        var bus = new FakeServiceBus(maxMessageBytes: 1000, messagesPerBatch: 10);
        var transport = bus.CreateTransport();

        var failures = await transport.SendBatchAsync("DomainEvents", rows, TestContext.CancellationToken);

        Assert.HasCount(1, failures);
        Assert.AreEqual(1, failures[0].Index);
        Assert.IsTrue(failures[0].Permanent, "an oversize message can never succeed; retrying only burns attempts");
        CollectionAssert.AreEqual(
            new[] { rows[0].Id.ToString(), rows[2].Id.ToString() },
            bus.Sent.SelectMany(b => b).Select(m => m.MessageId).ToList());
    }

    /// <summary>
    /// Each Service Bus send is atomic: when the second batch fails, the first stays sent and only its own rows
    /// and the ones never sent are reported as transient failures.
    /// </summary>
    [TestMethod]
    public async Task ServiceBus_SecondBatchFails_FirstBatchStaysSent_RestAreTransient()
    {
        var rows = Rows(5);
        var bus = new FakeServiceBus(maxMessageBytes: 1000, messagesPerBatch: 2, failOnSend: 2);
        var transport = bus.CreateTransport();

        var failures = await transport.SendBatchAsync("DomainEvents", rows, TestContext.CancellationToken);

        CollectionAssert.AreEqual(new[] { 2, 3, 4 }, failures.Select(f => f.Index).OrderBy(i => i).ToList());
        Assert.IsTrue(failures.All(f => !f.Permanent));
        CollectionAssert.AreEqual(
            new[] { rows[0].Id.ToString(), rows[1].Id.ToString() },
            bus.Sent.Single().Select(m => m.MessageId).ToList());
    }

    /// <summary>Service Bus producer spans continue the stored trace and end only after their batch was sent.</summary>
    [TestMethod]
    public async Task ServiceBus_ProducerSpan_ContinuesTheStoredTraceAndCoversTheSend()
    {
        using var listener = Listen(out var started);
        var rows = Rows(1, traceParent: StoredTraceParent);
        Activity? spanAtSend = null;
        var stoppedBeforeSend = true;
        var bus = new FakeServiceBus(maxMessageBytes: 1000, messagesPerBatch: 10,
            onSend: () =>
            {
                spanAtSend = ProducerFor(started, rows[0].Id);
                stoppedBeforeSend = spanAtSend.IsStopped;
            });
        var transport = bus.CreateTransport();

        using var drain = new Activity("OutboxMessage drain").SetIdFormat(ActivityIdFormat.W3C).Start();
        var failures = await transport.SendBatchAsync("DomainEvents", rows, TestContext.CancellationToken);

        Assert.IsEmpty(failures);
        Assert.IsNotNull(spanAtSend);
        Assert.IsFalse(stoppedBeforeSend, "the producer span ended before the send");
        AssertContinuesStoredTrace(spanAtSend, drain);
        Assert.IsTrue(spanAtSend.IsStopped);
        var sent = bus.Sent.Single().Single();
        Assert.AreEqual($"00-{StoredTraceId}-{spanAtSend.SpanId.ToHexString()}-01", sent.ApplicationProperties["traceparent"]);
        Assert.AreEqual(rows[0].EventType, sent.Subject);
        Assert.AreEqual(rows[0].EventVersion, sent.ApplicationProperties["EventVersion"]);
    }

    private static void AssertContinuesStoredTrace(Activity producer, Activity drain)
    {
        Assert.AreEqual(StoredTraceId, producer.TraceId.ToHexString(), "the producer must continue the staging trace");
        Assert.AreEqual(StoredSpanId, producer.ParentSpanId.ToHexString(), "the producer's parent is the staging span, not the drain");
        Assert.IsTrue(producer.Links.Any(l => l.Context.SpanId == drain.SpanId), "the drain span is kept as a link");
    }

    private static List<OutboxMessage> Rows(int count, string? traceParent = null) =>
        [.. Enumerable.Range(0, count).Select(i => new OutboxMessage
        {
            Id = Guid.CreateVersion7(),
            TenantId = Guid.CreateVersion7(),
            Destination = "DomainEvents",
            EventType = "TaskItemCreatedEvent",
            EventVersion = 1,
            Payload = $"{{\"Index\":{i}}}",
            TraceParent = traceParent,
            OccurredAtUtc = DateTimeOffset.UtcNow
        })];

    // Every live listener sees every activity, so tests running side by side share each other's spans.
    // Selecting by message id keeps each assertion on its own span.
    private static Activity ProducerFor(List<Activity> started, Guid messageId)
    {
        lock (started)
        {
            return started.Single(a => a.Kind == ActivityKind.Producer
                                       && (string?)a.GetTagItem("messaging.message.id") == messageId.ToString());
        }
    }

    private static ActivityListener Listen(out List<Activity> started)
    {
        var captured = new List<Activity>();
        started = captured;
        var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TaskFlowActivitySources.MessagingName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = activity =>
            {
                lock (captured) captured.Add(activity);
            }
        };
        ActivitySource.AddActivityListener(listener);
        return listener;
    }

    /// <summary>Mocked Service Bus client whose batches enforce a per-message size and a message count.</summary>
    private sealed class FakeServiceBus(int maxMessageBytes, int messagesPerBatch, int failOnSend = 0, Action? onSend = null)
    {
        private int _sends;

        public List<List<ServiceBusMessage>> Sent { get; } = [];

        public ServiceBusEventTransport CreateTransport()
        {
            var sender = new Mock<ServiceBusSender>();
            var stores = new Dictionary<ServiceBusMessageBatch, List<ServiceBusMessage>>();
            sender
                .Setup(s => s.CreateMessageBatchAsync(It.IsAny<CancellationToken>()))
                .Returns(() =>
                {
                    var store = new List<ServiceBusMessage>();
                    var batch = ServiceBusModelFactory.ServiceBusMessageBatch(
                        maxMessageBytes,
                        store,
                        tryAddCallback: m => m.Body.ToMemory().Length <= maxMessageBytes && store.Count < messagesPerBatch);
                    stores[batch] = store;
                    return new ValueTask<ServiceBusMessageBatch>(batch);
                });
            sender
                .Setup(s => s.SendMessagesAsync(It.IsAny<ServiceBusMessageBatch>(), It.IsAny<CancellationToken>()))
                .Returns<ServiceBusMessageBatch, CancellationToken>((batch, _) =>
                {
                    if (++_sends == failOnSend)
                        return Task.FromException(new ServiceBusException("namespace throttled", ServiceBusFailureReason.ServiceBusy));
                    onSend?.Invoke();
                    Sent.Add([.. stores[batch]]);
                    return Task.CompletedTask;
                });

            var client = new Mock<ServiceBusClient>();
            client.Setup(c => c.CreateSender(It.IsAny<string>())).Returns(sender.Object);
            var factory = new Mock<IAzureClientFactory<ServiceBusClient>>();
            factory.Setup(f => f.CreateClient("TaskFlowSBClient")).Returns(client.Object);

            return new ServiceBusEventTransport(factory.Object, NullLogger<ServiceBusEventTransport>.Instance);
        }
    }
}
