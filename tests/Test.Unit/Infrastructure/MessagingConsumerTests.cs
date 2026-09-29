using EF.Messaging;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Text;
using System.Text.Json;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Domain.Shared.Events;
using Test.Support;

namespace Test.Unit.Infrastructure;

/// <summary>
/// D-029 two-state inbox guard, run through EF.Messaging's IntegrationEventConsumerBase exactly as TaskFlow's
/// consumers derive from it, and TaskFlow's envelope reader configuration. Both are pure decision logic that an
/// integration test would only reach through a container, so they are asserted here directly; the store's own SQL
/// is proven against both providers in Test.Integration InboxStoreTests.
/// <para>
/// The claim timings are scaled down (200 ms lease) and run on the real clock: the renewal loop and the wait are
/// driven by the consumer's own timers, and every assertion waits on an observed event (a poll, a renewal),
/// not on a fixed sleep, except where the point is that nothing happens afterwards. A test whose outcome depends
/// on a lease lapsing or not decides that lapse without the real clock (<c>LiveForPolls</c>, <c>FrozenClock</c>), so a
/// stalled runner delays it but cannot flip it.
/// </para>
/// </summary>
[TestClass]
public sealed class MessagingConsumerTests
{
    private static readonly InboxClaimOptions Fast = new()
    {
        ClaimLease = TimeSpan.FromMilliseconds(200),
        WaitPollInterval = TimeSpan.FromMilliseconds(10),
        WaitMargin = TimeSpan.FromMilliseconds(50)
    };

    private static readonly IntegrationEnvelopeReaderOptions Reader = ReaderOptions();

    [TestMethod]
    [TestCategory("Unit")]
    public async Task Consumer_RunsOnce_ThenShortCircuitsTheRedelivery()
    {
        var ct = TestContext.CancellationToken;
        var inbox = new FakeInboxStore();
        var consumer = new CountingConsumer(inbox);
        var envelope = Envelope();

        Assert.AreEqual(ConsumeDisposition.Consumed, await consumer.HandleAsync(envelope, ct));
        Assert.AreEqual(ConsumeDisposition.Duplicate, await consumer.HandleAsync(envelope, ct));

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
    /// RabbitMQ crash semantics: the holder died without renewing, and the broker redelivers at once. The
    /// redelivery must wait the lease out and take the claim over - throwing at once would requeue it
    /// immediately and burn its whole delivery budget in milliseconds, dead-lettering it.
    /// </summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Consumer_AfterTheHolderCrashes_WaitsOutTheLeaseAndTakesTheClaimOver()
    {
        // The crashed claim lapses after a fixed number of polls rather than on the clock, and the waiter's bound
        // is far away: on the real clock a cold first call (JIT, meter setup) could outlast a 200 ms lease.
        var ct = TestContext.CancellationToken;
        var inbox = new FakeInboxStore { LiveForPolls = 3 };
        var envelope = Envelope();
        var crashed = await inbox.TryClaimAsync("test", envelope.Id, TimeSpan.FromHours(1), ct);
        Assert.AreEqual(InboxClaimStatus.Acquired, crashed.Status);

        var redelivery = new CountingConsumer(inbox, options: new InboxClaimOptions
        {
            ClaimLease = TimeSpan.FromMinutes(5),
            WaitPollInterval = Fast.WaitPollInterval,
            WaitMargin = Fast.WaitMargin
        });
        await redelivery.HandleAsync(envelope, ct);

        Assert.AreEqual(1, redelivery.Consumed, "the redelivery runs the effect exactly once, without a transport retry");
        Assert.AreEqual(3, inbox.InProgressPolls, "the redelivery waited on the live claim before taking it over");
        Assert.AreEqual(FakeInboxStore.State.Completed, inbox.StateOf("test", envelope.Id));
    }

    /// <summary>A holder that finishes while the redelivery waits turns it into a duplicate: ack, do not run.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Consumer_WhenTheHolderCompletesDuringTheWait_AcksWithoutRunning()
    {
        var ct = TestContext.CancellationToken;
        var inbox = new FakeInboxStore();
        var envelope = Envelope();
        var holder = await inbox.TryClaimAsync("test", envelope.Id, TimeSpan.FromHours(1), ct);
        var waiter = new CountingConsumer(inbox);

        var waiting = waiter.HandleAsync(envelope, ct);
        await inbox.FirstInProgressPoll.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.IsTrue(await inbox.CompleteAsync("test", envelope.Id, holder.ClaimToken, ct));
        await waiting;

        Assert.AreEqual(0, waiter.Consumed, "the holder already ran the effect");
    }

    /// <summary>
    /// The holder renews while it runs, so a handler slower than the lease keeps its claim: a redelivery during
    /// the run still finds it in progress and cannot take it over.
    /// </summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Consumer_WithAHandlerLongerThanTheLease_RenewalKeepsTheClaim()
    {
        // The store's clock is frozen and moved only here, so a stalled runner cannot lapse the lease between two
        // renewals; the renewal timer itself still runs, and each step waits on an observed renewal.
        var ct = TestContext.CancellationToken;
        var inbox = new FakeInboxStore { FrozenClock = true };
        var envelope = Envelope();
        InboxClaimStatus? duringRun = null;
        var holder = new CountingConsumer(inbox)
        {
            During = async () =>
            {
                await inbox.RenewalsReached(1).WaitAsync(TimeSpan.FromSeconds(10), ct);
                // Past the initial lease: without a later renewal the claim now reads as expired.
                var renewals = inbox.Advance(Fast.ClaimLease + Fast.WaitMargin);
                await inbox.RenewalsReached(renewals + 1).WaitAsync(TimeSpan.FromSeconds(10), ct);
                duringRun = (await inbox.TryClaimAsync("test", envelope.Id, Fast.ClaimLease, ct)).Status;
            }
        };

        await holder.HandleAsync(envelope, ct);

        Assert.AreEqual(InboxClaimStatus.InProgress, duringRun, "a renewed claim must not be taken over mid-run");
        Assert.AreEqual(1, holder.Consumed);
        Assert.AreEqual(FakeInboxStore.State.Completed, inbox.StateOf("test", envelope.Id));
    }

    /// <summary>
    /// A claim that stays live for the whole wait belongs to a holder that is alive and renewing: the waiter
    /// gives up after one lease plus the margin and reports InProgress, which every transport retries (RabbitMQ
    /// requeue, Service Bus abandon), without running the effect.
    /// </summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Consumer_WhenTheClaimStaysLivePastTheWaitBound_ReportsInProgress()
    {
        var ct = TestContext.CancellationToken;
        var inbox = new FakeInboxStore();
        var envelope = Envelope();
        await inbox.TryClaimAsync("test", envelope.Id, TimeSpan.FromHours(1), ct);
        var waiter = new CountingConsumer(inbox);
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        var disposition = await waiter.HandleAsync(envelope, ct);

        Assert.AreEqual(ConsumeDisposition.InProgress, disposition);
        Assert.AreEqual(0, waiter.Consumed);
        Assert.IsGreaterThanOrEqualTo(Fast.WaitBound, System.Diagnostics.Stopwatch.GetElapsedTime(started),
            "the waiter must hold the delivery for the whole bound before giving up");
    }

    /// <summary>Renewal ends before the claim is completed, and nothing renews it afterwards.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Consumer_AfterCompletion_RenewalHasStopped()
    {
        var ct = TestContext.CancellationToken;
        var inbox = new FakeInboxStore();
        var consumer = new CountingConsumer(inbox)
        {
            During = () => inbox.RenewalsReached(2).WaitAsync(TimeSpan.FromSeconds(10), ct)
        };

        await consumer.HandleAsync(Envelope(), ct);
        var renewalsAtCompletion = inbox.Renewals;

        // The point is that nothing happens afterwards, so this one waits: three renewal periods.
        await Task.Delay(Fast.RenewalInterval * 3, ct);

        Assert.AreEqual(renewalsAtCompletion, inbox.Renewals, "a renewal ran after the claim was settled");
        Assert.AreEqual("complete", inbox.Events.Last(), "complete is the last store call");
    }

    /// <summary>A failing renewal is logged and the handler still finishes and completes its claim.</summary>
    [TestMethod]
    [TestCategory("Unit")]
    public async Task Consumer_WhenRenewalFails_LogsAndStillCompletes()
    {
        var ct = TestContext.CancellationToken;
        var inbox = new FakeInboxStore { RenewThrows = true };
        var logger = new RecordingLogger();
        var envelope = Envelope();
        var consumer = new CountingConsumer(inbox, logger)
        {
            During = () => inbox.RenewalsReached(2).WaitAsync(TimeSpan.FromSeconds(10), ct)
        };

        await consumer.HandleAsync(envelope, ct);

        Assert.AreEqual(1, consumer.Consumed);
        Assert.AreEqual(FakeInboxStore.State.Completed, inbox.StateOf("test", envelope.Id));
        Assert.IsTrue(logger.Entries.Any(e => e.Level == LogLevel.Warning && e.Exception is TimeoutException),
            "a failed renewal must be visible");
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
        var consumer = new CountingConsumer(inbox) { During = () => delivery.CancelAsync() };
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

        var disposition = await consumer.HandleAsync(Envelope() with { Type = nameof(TaskItemCompletedEvent) }, TestContext.CancellationToken);

        Assert.AreEqual(ConsumeDisposition.NotHandled, disposition);
        Assert.IsEmpty(inbox.Events);
        Assert.AreEqual(0, consumer.Consumed);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void ClaimOptions_Defaults_AreTheDocumentedTimings()
    {
        var defaults = new InboxClaimOptions();

        Assert.AreEqual(TimeSpan.FromSeconds(60), defaults.ClaimLease);
        Assert.AreEqual(TimeSpan.FromSeconds(20), defaults.RenewalInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(1), defaults.WaitPollInterval);
        Assert.AreEqual(TimeSpan.FromSeconds(65), defaults.WaitBound);
        Assert.IsTrue(defaults.IsValid());
        Assert.IsFalse(new InboxClaimOptions { WaitPollInterval = defaults.WaitBound }.IsValid(),
            "a poll no shorter than the whole wait bound never re-reads the claim");
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EnvelopeReader_DeadLettersMalformedAndUnsupportedBodies()
    {
        Assert.IsFalse(IntegrationEnvelopeReader.TryRead("not json"u8, Reader, out _, out var malformed));
        Assert.AreEqual(IntegrationEnvelopeReader.MalformedReason, malformed);

        var unknown = JsonSerializer.Serialize(Envelope() with { Type = "SomeFutureEvent" });
        Assert.IsFalse(IntegrationEnvelopeReader.TryRead(Encoding.UTF8.GetBytes(unknown), Reader, out _, out var unsupported));
        Assert.AreEqual(IntegrationEnvelopeReader.UnsupportedReason, unsupported);

        var good = JsonSerializer.Serialize(Envelope());
        Assert.IsTrue(IntegrationEnvelopeReader.TryRead(Encoding.UTF8.GetBytes(good), Reader, out var envelope, out var failure));
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

        Assert.IsFalse(IntegrationEnvelopeReader.TryRead(Encoding.UTF8.GetBytes(invalid), Reader, out _, out var failure));
        Assert.AreEqual(IntegrationEnvelopeReader.MalformedReason, failure);
    }

    [TestMethod]
    [TestCategory("Unit")]
    public void EnvelopeReader_DeadLettersEmptyMessageId()
    {
        var invalid = JsonSerializer.Serialize(Envelope() with { Id = Guid.Empty });

        Assert.IsFalse(IntegrationEnvelopeReader.TryRead(Encoding.UTF8.GetBytes(invalid), Reader, out _, out var failure));
        Assert.AreEqual(IntegrationEnvelopeReader.MalformedReason, failure);
    }

    public TestContext TestContext { get; set; } = null!;

    private static IntegrationEnvelopeReaderOptions ReaderOptions()
    {
        var options = new IntegrationEnvelopeReaderOptions();
        TaskFlowIntegrationEvents.ConfigureReader(options);
        return options;
    }

    private static IntegrationEventEnvelope Envelope() => TaskFlowIntegrationEvents.Envelope(
        new TaskItemCreatedEvent(Guid.CreateVersion7(), TestConstants.TenantId, "guarded"),
        new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero),
        correlationId: null,
        id: Guid.Parse("0199e3f0-0000-7000-8000-000000000001"));

    /// <summary>In-memory two-state inbox with the store's claim rules on the real clock; thread safe.</summary>
    private sealed class FakeInboxStore : IInboxStore
    {
        public enum State { InProgress, Completed }

        private readonly Lock _gate = new();
        private readonly Dictionary<(string, Guid), (Guid Token, DateTimeOffset Expires, bool Completed)> _claims = [];
        private readonly List<string> _events = [];
        private readonly List<(int Count, TaskCompletionSource Signal)> _renewalWaits = [];
        private readonly TaskCompletionSource _firstInProgressPoll = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _renewals;
        private int _inProgressPolls;
        private DateTimeOffset? _frozenNow;

        /// <summary>When set, the store's clock stands still until <see cref="Advance"/> moves it.</summary>
        public bool FrozenClock
        {
            init => _frozenNow = value ? new DateTimeOffset(2026, 9, 4, 12, 0, 0, TimeSpan.Zero) : null;
        }

        private DateTimeOffset Now => _frozenNow ?? DateTimeOffset.UtcNow;

        /// <summary>Moves the frozen clock and returns the renewals attempted before the move.</summary>
        public int Advance(TimeSpan by)
        {
            lock (_gate)
            {
                _frozenNow = (_frozenNow ?? throw new InvalidOperationException("Advance needs FrozenClock.")) + by;
                return _renewals;
            }
        }

        /// <summary>When set, a held claim reads as live for this many in-progress polls, then as expired.</summary>
        public int? LiveForPolls { get; init; }
        public bool CompleteResult { get; init; } = true;
        public bool ReleaseThrows { get; init; }
        public bool RenewThrows { get; init; }
        public bool CompleteSawCancelledToken { get; private set; }
        public int Renewals => Volatile.Read(ref _renewals);
        public int InProgressPolls => Volatile.Read(ref _inProgressPolls);
        public Task FirstInProgressPoll => _firstInProgressPoll.Task;

        public IReadOnlyList<string> Events
        {
            get { lock (_gate) return [.. _events]; }
        }

        /// <summary>Completes once <paramref name="count"/> renewals were attempted.</summary>
        public Task RenewalsReached(int count)
        {
            lock (_gate)
            {
                var signal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_renewals >= count) signal.SetResult();
                else _renewalWaits.Add((count, signal));
                return signal.Task;
            }
        }

        public State? StateOf(string consumer, Guid messageId)
        {
            lock (_gate)
            {
                return _claims.TryGetValue((consumer, messageId), out var claim)
                    ? claim.Completed ? State.Completed : State.InProgress
                    : null;
            }
        }

        public Task<InboxClaim> TryClaimAsync(string consumer, Guid messageId, TimeSpan leaseDuration, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _events.Add("claim");
                var now = Now;
                var key = (consumer, messageId);
                if (_claims.TryGetValue(key, out var claim))
                {
                    if (claim.Completed) return Task.FromResult(new InboxClaim(InboxClaimStatus.Duplicate, Guid.Empty));
                    if (LiveForPolls is { } polls ? _inProgressPolls < polls : claim.Expires >= now)
                    {
                        Interlocked.Increment(ref _inProgressPolls);
                        _firstInProgressPoll.TrySetResult();
                        return Task.FromResult(new InboxClaim(InboxClaimStatus.InProgress, Guid.Empty));
                    }
                }

                var token = Guid.NewGuid();
                _claims[key] = (token, now + leaseDuration, false);
                return Task.FromResult(new InboxClaim(InboxClaimStatus.Acquired, token));
            }
        }

        public Task<bool> RenewAsync(string consumer, Guid messageId, Guid claimToken, TimeSpan leaseDuration, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _events.Add("renew");
                _renewals++;
                foreach (var wait in _renewalWaits.Where(w => _renewals >= w.Count).ToList())
                {
                    wait.Signal.TrySetResult();
                    _renewalWaits.Remove(wait);
                }

                if (RenewThrows) throw new TimeoutException("database unavailable");
                var key = (consumer, messageId);
                if (!_claims.TryGetValue(key, out var claim) || claim.Token != claimToken || claim.Completed)
                    return Task.FromResult(false);
                _claims[key] = (claimToken, Now + leaseDuration, false);
                return Task.FromResult(true);
            }
        }

        public Task<bool> CompleteAsync(string consumer, Guid messageId, Guid claimToken, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _events.Add("complete");
                CompleteSawCancelledToken |= ct.IsCancellationRequested;
                if (!CompleteResult) return Task.FromResult(false);
                var key = (consumer, messageId);
                if (!_claims.TryGetValue(key, out var claim) || claim.Token != claimToken) return Task.FromResult(false);
                _claims[key] = (claimToken, claim.Expires, true);
                return Task.FromResult(true);
            }
        }

        public Task<bool> ReleaseAsync(string consumer, Guid messageId, Guid claimToken, CancellationToken ct = default)
        {
            lock (_gate)
            {
                _events.Add("release");
                if (ReleaseThrows) throw new TimeoutException("database unavailable");
                var key = (consumer, messageId);
                return Task.FromResult(_claims.TryGetValue(key, out var claim) && claim.Token == claimToken && _claims.Remove(key));
            }
        }

        public Task<int> PurgeAsync(DateTimeOffset cutoffUtc, CancellationToken ct = default)
        {
            lock (_gate)
            {
                var removed = _claims.Count;
                _claims.Clear();
                return Task.FromResult(removed);
            }
        }
    }

    private sealed class CountingConsumer(IInboxStore inbox, ILogger? logger = null, InboxClaimOptions? options = null)
        : IntegrationEventConsumerBase(inbox, new MessagingMetrics(), logger ?? NullLogger.Instance, Options.Create(options ?? Fast))
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
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
        {
            get { lock (_entries) return [.. _entries]; }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries) _entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
