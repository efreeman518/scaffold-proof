using EF.BackgroundServices.Leased;
using EF.Messaging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Text.Json;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.MessageHandlers.Consumers;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Infrastructure.Repositories;
using TaskFlow.Observability.Meters;
using TaskFlow.Scheduler.Workers;
using Test.Support;

namespace Test.Unit.Infrastructure;

/// <summary>
/// D-029 inbox guard and the drain loop's poll policy. Both are pure decision logic that an integration test
/// would only reach through a container, so they are asserted here directly.
/// </summary>
[TestClass]
public sealed class MessagingConsumerTests
{
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Consumer_RunsOnce_ThenShortCircuitsTheRedelivery()
    {
        var ct = TestContext.CancellationToken;
        var inbox = new FakeInboxStore();
        var consumer = new CountingConsumer(inbox);
        var envelope = Envelope();

        await consumer.HandleAsync(envelope, ct);
        await consumer.HandleAsync(envelope, ct);

        Assert.AreEqual(1, consumer.Consumed, "the second delivery of the same MessageId must be skipped");
        Assert.AreEqual(1, inbox.Claims.Count);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Consumer_ThatThrows_ReleasesTheClaimSoTheRetryRuns()
    {
        var ct = TestContext.CancellationToken;
        var inbox = new FakeInboxStore();
        var consumer = new CountingConsumer(inbox) { Throw = true };
        var envelope = Envelope();

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => consumer.HandleAsync(envelope, ct));
        Assert.AreEqual(0, inbox.Claims.Count, "a failed unit of work must not leave its claim behind");

        consumer.Throw = false;
        await consumer.HandleAsync(envelope, ct);
        Assert.AreEqual(1, consumer.Consumed);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EnvelopeReader_DeadLettersMalformedAndUnsupportedBodies()
    {
        Assert.IsFalse(IntegrationEnvelopeReader.TryRead("not json"u8, out _, out var malformed));
        Assert.AreEqual(IntegrationEnvelopeReader.MalformedReason, malformed);

        var unknown = JsonSerializer.Serialize(Envelope() with { Type = "SomeFutureEvent" });
        Assert.IsFalse(IntegrationEnvelopeReader.TryRead(Encoding.UTF8.GetBytes(unknown), out _, out var unsupported));
        Assert.AreEqual(IntegrationEnvelopeReader.UnsupportedReason, unsupported);

        var good = JsonSerializer.Serialize(Envelope());
        Assert.IsTrue(IntegrationEnvelopeReader.TryRead(Encoding.UTF8.GetBytes(good), out var envelope, out var failure));
        Assert.IsNull(failure);
        Assert.AreEqual(nameof(TaskItemCreatedEvent), envelope!.Type);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void LeasedWorker_PollsImmediatelyOnAFullBatch_AndBacksOffWhenIdle()
    {
        var oneSecond = TimeSpan.FromSeconds(1);
        var fiveSeconds = TimeSpan.FromSeconds(5);

        // The drain's own settings: a 1s floor, a 5s idle ceiling and a 50-row claim (package request 11).
        var options = new OutboxDispatcherSettings();
        Assert.AreEqual(oneSecond, options.PollInterval);
        Assert.AreEqual(fiveSeconds, options.IdleBackoffMax);
        Assert.AreEqual(50, options.BatchSize);

        // A full batch means more work is waiting: do not sleep at all.
        Assert.AreEqual(TimeSpan.Zero,
            LeasedWorkerBase<OutboxDispatcherSettings>.NextDelay(fiveSeconds, processed: 50, options));

        // Partial work resets to the floor.
        Assert.AreEqual(oneSecond,
            LeasedWorkerBase<OutboxDispatcherSettings>.NextDelay(fiveSeconds, processed: 7, options));

        // Idle doubles up to the ceiling and stops there.
        var delay = oneSecond;
        var observed = new List<TimeSpan>();
        for (var i = 0; i < 5; i++)
        {
            delay = LeasedWorkerBase<OutboxDispatcherSettings>.NextDelay(delay, processed: 0, options);
            observed.Add(delay);
        }

        CollectionAssert.AreEqual(
            new[] { TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), fiveSeconds, fiveSeconds, fiveSeconds },
            observed);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ReleaseBackoff_GrowsExponentially_AndIsCappedWithJitter()
    {
        // 2s * 2^(n-1) plus up to 20% jitter, capped at five minutes.
        var first = OperationalWorkRepository.Backoff(1);
        Assert.IsTrue(first >= TimeSpan.FromSeconds(2) && first <= TimeSpan.FromSeconds(2.4), $"was {first}");

        var fourth = OperationalWorkRepository.Backoff(4);
        Assert.IsTrue(fourth >= TimeSpan.FromSeconds(16) && fourth <= TimeSpan.FromSeconds(19.2), $"was {fourth}");

        var capped = OperationalWorkRepository.Backoff(20);
        Assert.IsTrue(capped >= TimeSpan.FromMinutes(5) && capped <= TimeSpan.FromMinutes(6), $"was {capped}");

        Assert.AreEqual(1024, OperationalWorkRepository.Truncate(new string('x', 5000)).Length);
    }

    public TestContext TestContext { get; set; } = null!;

    private static IntegrationEventEnvelope Envelope() => TaskFlowIntegrationEvents.Envelope(
        new TaskItemCreatedEvent(Guid.CreateVersion7(), TestConstants.TenantId, "guarded"),
        new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
        correlationId: null,
        id: Guid.Parse("0199e3f0-0000-7000-8000-000000000001"));

    private sealed class FakeInboxStore : IInboxStore
    {
        public HashSet<(string Consumer, Guid MessageId)> Claims { get; } = [];

        public Task<bool> TryClaimAsync(string consumer, Guid messageId, CancellationToken ct = default)
            => Task.FromResult(Claims.Add((consumer, messageId)));

        public Task ReleaseAsync(string consumer, Guid messageId, CancellationToken ct = default)
        {
            Claims.Remove((consumer, messageId));
            return Task.CompletedTask;
        }

        public Task<int> PurgeProcessedAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default)
        {
            var removed = Claims.Count;
            Claims.Clear();
            return Task.FromResult(removed);
        }
    }

    private sealed class CountingConsumer(IInboxStore inbox)
        : IntegrationEventConsumer(inbox, new MessagingMetrics(), NullLogger.Instance)
    {
        public int Consumed { get; private set; }

        public bool Throw { get; set; }

        public override string ConsumerName => "test";

        public override bool Handles(string eventType) => eventType == nameof(TaskItemCreatedEvent);

        protected override Task ConsumeAsync(IntegrationEventEnvelope envelope, CancellationToken ct)
        {
            if (Throw) throw new InvalidOperationException("transient");
            Consumed++;
            return Task.CompletedTask;
        }
    }
}
