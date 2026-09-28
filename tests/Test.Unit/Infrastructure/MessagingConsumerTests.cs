using EF.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text;
using System.Text.Json;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.MessageHandlers.Consumers;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Infrastructure.Repositories;
using TaskFlow.Observability.Meters;
using Test.Support;

namespace Test.Unit.Infrastructure;

/// <summary>
/// D-029 two-state inbox guard and the release backoff. Both are pure decision logic that an integration test
/// would only reach through a container, so they are asserted here directly; the store's own SQL is proven
/// against both providers in Test.Integration InboxStoreTests.
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
        Assert.AreEqual(FakeInboxStore.State.Completed, inbox.StateOf("test", envelope.Id));
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
        Assert.IsNull(inbox.StateOf("test", envelope.Id), "a failed unit of work must not leave its claim behind");

        consumer.Throw = false;
        await consumer.HandleAsync(envelope, ct);
        Assert.AreEqual(1, consumer.Consumed);
    }

    /// <summary>
    /// The claim is not a completion: until the effect has run, a second delivery of the same message (a Service
    /// Bus redelivery after a lock loss, a RabbitMQ requeue) must be thrown back for retry, not acknowledged as a
    /// duplicate - acknowledging it would lose the effect if the first delivery then failed.
    /// </summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Consumer_WhileAnotherDeliveryIsProcessing_ThrowsInProgressAndDoesNotConsume()
    {
        var ct = TestContext.CancellationToken;
        var inbox = new FakeInboxStore();
        var envelope = Envelope();
        var concurrent = new CountingConsumer(inbox);
        InboxClaimInProgressException? observed = null;
        var first = new CountingConsumer(inbox)
        {
            During = async () => observed = await Assert.ThrowsExactlyAsync<InboxClaimInProgressException>(
                () => concurrent.HandleAsync(envelope, ct))
        };

        await first.HandleAsync(envelope, ct);

        Assert.IsNotNull(observed);
        Assert.AreEqual(envelope.Id, observed.MessageId);
        Assert.AreEqual(0, concurrent.Consumed, "the in-progress delivery must not run the effect");
        Assert.AreEqual(1, first.Consumed);
        Assert.AreEqual(FakeInboxStore.State.Completed, inbox.StateOf("test", envelope.Id),
            "the claim is completed only after the effect ran");

        // Once completed, the same redelivery is a plain duplicate.
        await concurrent.HandleAsync(envelope, ct);
        Assert.AreEqual(0, concurrent.Consumed);
    }

    /// <summary>
    /// A crash between claim and complete leaves the claim in progress; once its lease expires the redelivery
    /// takes it over and the effect runs, instead of being skipped forever as it was with the one-state inbox.
    /// </summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Consumer_AfterACrashedDeliveryLeaseExpires_TakesTheClaimOverAndRuns()
    {
        var ct = TestContext.CancellationToken;
        var inbox = new FakeInboxStore();
        var envelope = Envelope();
        var crashedToken = await inbox.TryClaimAsync("test", envelope.Id, TimeSpan.FromMinutes(10), ct);
        Assert.AreEqual(InboxClaimStatus.Acquired, crashedToken.Status);

        var redelivery = new CountingConsumer(inbox);
        await Assert.ThrowsExactlyAsync<InboxClaimInProgressException>(() => redelivery.HandleAsync(envelope, ct));

        inbox.ExpireLeases();
        await redelivery.HandleAsync(envelope, ct);

        Assert.AreEqual(1, redelivery.Consumed);
        Assert.AreEqual(FakeInboxStore.State.Completed, inbox.StateOf("test", envelope.Id));
        Assert.AreEqual(TimeSpan.FromMinutes(10), inbox.LastLease, "the consumer's claim lease reaches the store");
    }

    /// <summary>The effect ran, so a claim lost to a takeover is logged, not failed: failing would re-run it.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Consumer_WhoseClaimWasTakenOver_StillSucceedsAndWarns()
    {
        var ct = TestContext.CancellationToken;
        var inbox = new FakeInboxStore { CompleteResult = false };
        var logger = new RecordingLogger();
        var consumer = new CountingConsumer(inbox, logger);

        await consumer.HandleAsync(Envelope(), ct);

        Assert.AreEqual(1, consumer.Consumed);
        Assert.IsTrue(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Message.Contains("may have run twice")),
            "a lost claim must be visible: the effect may have run twice");
    }

    /// <summary>A release that fails too must not mask why the consume failed; the lease recovers the claim.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Consumer_WhenReleaseAlsoFails_RethrowsTheOriginalFailure()
    {
        var ct = TestContext.CancellationToken;
        var inbox = new FakeInboxStore { ReleaseThrows = true };
        var logger = new RecordingLogger();
        var consumer = new CountingConsumer(inbox, logger) { Throw = true };

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => consumer.HandleAsync(Envelope(), ct));

        Assert.AreEqual("transient", thrown.Message, "the consume failure, not the release failure, propagates");
        Assert.IsTrue(logger.Entries.Any(e => e.Level == LogLevel.Error && e.Exception is AggregateException));
    }

    /// <summary>Settlement of the claim ignores the delivery token: shutdown after the effect must not strand it.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Consumer_CancelledAfterTheEffect_StillCompletesTheClaim()
    {
        using var delivery = new CancellationTokenSource();
        var inbox = new FakeInboxStore();
        var consumer = new CountingConsumer(inbox) { During = () => { delivery.Cancel(); return Task.CompletedTask; } };
        var envelope = Envelope();

        await consumer.HandleAsync(envelope, delivery.Token);

        Assert.AreEqual(FakeInboxStore.State.Completed, inbox.StateOf("test", envelope.Id));
        Assert.IsFalse(inbox.CompleteSawCancelledToken);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Consumer_ForAnEventItDoesNotHandle_NeverTouchesTheInbox()
    {
        var inbox = new FakeInboxStore();
        var consumer = new CountingConsumer(inbox);

        await consumer.HandleAsync(Envelope() with { Type = nameof(TaskItemCompletedEvent) }, TestContext.CancellationToken);

        Assert.AreEqual(0, inbox.Calls);
        Assert.AreEqual(0, consumer.Consumed);
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
    [DataRow(null)]
    [DataRow("")]
    [DataRow(" ")]
    public void EnvelopeReader_DeadLettersMissingEventType(string? type)
    {
        var invalid = JsonSerializer.Serialize(Envelope() with { Type = type! });

        Assert.IsFalse(IntegrationEnvelopeReader.TryRead(Encoding.UTF8.GetBytes(invalid), out _, out var failure));
        Assert.AreEqual(IntegrationEnvelopeReader.MalformedReason, failure);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EnvelopeReader_DeadLettersEmptyMessageId()
    {
        var invalid = JsonSerializer.Serialize(Envelope() with { Id = Guid.Empty });

        Assert.IsFalse(IntegrationEnvelopeReader.TryRead(Encoding.UTF8.GetBytes(invalid), out _, out var failure));
        Assert.AreEqual(IntegrationEnvelopeReader.MalformedReason, failure);
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

    /// <summary>In-memory two-state inbox with the store's claim rules and switchable failure modes.</summary>
    private sealed class FakeInboxStore : IInboxStore
    {
        public enum State { InProgress, Expired, Completed }

        private readonly Dictionary<(string, Guid), (State State, Guid Token)> _claims = [];

        public bool CompleteResult { get; init; } = true;
        public bool ReleaseThrows { get; init; }
        public bool CompleteSawCancelledToken { get; private set; }
        public TimeSpan LastLease { get; private set; }
        public int Calls { get; private set; }

        public State? StateOf(string consumer, Guid messageId) =>
            _claims.TryGetValue((consumer, messageId), out var claim) ? claim.State : null;

        public void ExpireLeases()
        {
            foreach (var key in _claims.Keys.ToList())
            {
                if (_claims[key].State == State.InProgress) _claims[key] = (State.Expired, _claims[key].Token);
            }
        }

        public Task<InboxClaim> TryClaimAsync(string consumer, Guid messageId, TimeSpan leaseDuration, CancellationToken ct = default)
        {
            Calls++;
            LastLease = leaseDuration;
            var key = (consumer, messageId);
            if (_claims.TryGetValue(key, out var claim) && claim.State != State.Expired)
            {
                return Task.FromResult(new InboxClaim(
                    claim.State == State.Completed ? InboxClaimStatus.Duplicate : InboxClaimStatus.InProgress, Guid.Empty));
            }

            var token = Guid.NewGuid();
            _claims[key] = (State.InProgress, token);
            return Task.FromResult(new InboxClaim(InboxClaimStatus.Acquired, token));
        }

        public Task<bool> CompleteAsync(string consumer, Guid messageId, Guid claimToken, CancellationToken ct = default)
        {
            Calls++;
            CompleteSawCancelledToken |= ct.IsCancellationRequested;
            if (!CompleteResult) return Task.FromResult(false);
            var key = (consumer, messageId);
            if (!_claims.TryGetValue(key, out var claim) || claim.Token != claimToken) return Task.FromResult(false);
            _claims[key] = (State.Completed, claimToken);
            return Task.FromResult(true);
        }

        public Task<bool> ReleaseAsync(string consumer, Guid messageId, Guid claimToken, CancellationToken ct = default)
        {
            Calls++;
            if (ReleaseThrows) throw new TimeoutException("database unavailable");
            var key = (consumer, messageId);
            return Task.FromResult(_claims.TryGetValue(key, out var claim) && claim.Token == claimToken && _claims.Remove(key));
        }

        public Task<int> PurgeAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default)
        {
            var removed = _claims.Count;
            _claims.Clear();
            return Task.FromResult(removed);
        }
    }

    private sealed class CountingConsumer(IInboxStore inbox, ILogger? logger = null)
        : IntegrationEventConsumer(inbox, new MessagingMetrics(), logger ?? NullLogger.Instance)
    {
        public int Consumed { get; private set; }

        public bool Throw { get; set; }

        /// <summary>Runs inside ConsumeAsync, while the claim is held.</summary>
        public Func<Task>? During { get; init; }

        public override string ConsumerName => "test";

        public override bool Handles(string eventType) => eventType == nameof(TaskItemCreatedEvent);

        protected override async Task ConsumeAsync(IntegrationEventEnvelope envelope, CancellationToken ct)
        {
            if (During is not null) await During();
            if (Throw) throw new InvalidOperationException("transient");
            Consumed++;
        }
    }

    private sealed class RecordingLogger : ILogger
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
