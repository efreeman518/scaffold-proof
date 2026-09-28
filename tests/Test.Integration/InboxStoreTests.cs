using EF.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.MessageHandlers.Consumers;
using TaskFlow.Domain.Shared.Events;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Repositories;
using TaskFlow.Observability.Meters;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// D-029 two-state inbox against a real database, on whichever provider the lane selected. The claim decides
/// whether a consumer's effect runs zero, one or two times, and it is expressed as provider-neutral single
/// statements (upsert-if-absent, conditional takeover), so the race behavior has to be proven on both providers.
/// Component tier: contexts directly against the standalone database Testcontainer.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class InboxStoreTests
{
    private const string Consumer = "projection";
    private static readonly TimeSpan Lease = TimeSpan.FromSeconds(60);

    /// <summary>Inconclusive without a container runtime; fails when the database container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("SQL", DbContainerFixture.StartupError);

    /// <summary>Of many deliveries racing for one message, exactly one acquires; the rest are told to retry.</summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task ConcurrentClaims_ExactlyOneAcquires_RestAreInProgress()
    {
        var ct = TestContext.CancellationToken;
        var connString = await MigratedDatabaseAsync("inboxrace", ct);
        var messageId = Guid.CreateVersion7();

        var claims = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(async () =>
        {
            await using var db = DbContainerFixture.CreateTrxnContext(connString);
            return await Store(db, connString).TryClaimAsync(Consumer, messageId, Lease, ct);
        }, ct)));

        Assert.AreEqual(1, claims.Count(c => c.Status == InboxClaimStatus.Acquired));
        Assert.AreEqual(15, claims.Count(c => c.Status == InboxClaimStatus.InProgress),
            "an uncompleted claim is in progress, never a duplicate: acknowledging it could lose the effect");
    }

    /// <summary>Complete turns redeliveries into duplicates; release lets the retry acquire at once.</summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Complete_MakesRedeliveriesDuplicates_Release_LetsTheRetryAcquire()
    {
        var ct = TestContext.CancellationToken;
        var connString = await MigratedDatabaseAsync("inboxstates", ct);
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        var inbox = Store(db, connString);

        var released = Guid.CreateVersion7();
        var failed = await inbox.TryClaimAsync(Consumer, released, Lease, ct);
        Assert.IsTrue(await inbox.ReleaseAsync(Consumer, released, failed.ClaimToken, ct));
        Assert.AreEqual(InboxClaimStatus.Acquired, (await inbox.TryClaimAsync(Consumer, released, Lease, ct)).Status);

        var completed = Guid.CreateVersion7();
        var claim = await inbox.TryClaimAsync(Consumer, completed, Lease, ct);
        Assert.IsTrue(await inbox.CompleteAsync(Consumer, completed, claim.ClaimToken, ct));
        Assert.AreEqual(InboxClaimStatus.Duplicate, (await inbox.TryClaimAsync(Consumer, completed, Lease, ct)).Status);
        Assert.IsFalse(await inbox.ReleaseAsync(Consumer, completed, claim.ClaimToken, ct), "a completed claim is never released");

        // Consumers are independent: another consumer of the same message has its own claim.
        Assert.AreEqual(InboxClaimStatus.Acquired, (await inbox.TryClaimAsync("ai-review", completed, Lease, ct)).Status);
    }

    /// <summary>
    /// A delivery that crashed between claim and complete leaves an in-progress claim; after its lease expires the
    /// redelivery takes it over, and the crashed delivery's token can no longer complete it.
    /// </summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task ExpiredClaim_IsTakenOver_AndTheOldTokenLosesIt()
    {
        var ct = TestContext.CancellationToken;
        var connString = await MigratedDatabaseAsync("inboxtakeover", ct);
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var inbox = Store(db, connString, clock);
        var messageId = Guid.CreateVersion7();

        var crashed = await inbox.TryClaimAsync(Consumer, messageId, Lease, ct);
        Assert.AreEqual(InboxClaimStatus.InProgress, (await inbox.TryClaimAsync(Consumer, messageId, Lease, ct)).Status);

        clock.Advance(Lease + TimeSpan.FromSeconds(1));
        var takeover = await inbox.TryClaimAsync(Consumer, messageId, Lease, ct);

        Assert.AreEqual(InboxClaimStatus.Acquired, takeover.Status);
        Assert.AreNotEqual(crashed.ClaimToken, takeover.ClaimToken);
        Assert.IsFalse(await inbox.CompleteAsync(Consumer, messageId, crashed.ClaimToken, ct),
            "the taken-over delivery must learn its claim is gone");
        Assert.IsTrue(await inbox.CompleteAsync(Consumer, messageId, takeover.ClaimToken, ct));
    }

    /// <summary>
    /// The holder's renewal keeps a claim live past its initial lease; a renewal under a token that lost the claim,
    /// or after completion, changes nothing.
    /// </summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Renew_KeepsTheClaimLive_AndFailsOnceTakenOverOrCompleted()
    {
        var ct = TestContext.CancellationToken;
        var connString = await MigratedDatabaseAsync("inboxrenew", ct);
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var inbox = Store(db, connString, clock);
        var messageId = Guid.CreateVersion7();
        var lease = TimeSpan.FromSeconds(60);

        var holder = await inbox.TryClaimAsync(Consumer, messageId, lease, ct);
        clock.Advance(TimeSpan.FromSeconds(50));
        Assert.IsTrue(await inbox.RenewAsync(Consumer, messageId, holder.ClaimToken, lease, ct));

        clock.Advance(TimeSpan.FromSeconds(20)); // 70 s after the claim, past the initial lease
        Assert.AreEqual(InboxClaimStatus.InProgress, (await inbox.TryClaimAsync(Consumer, messageId, lease, ct)).Status,
            "a renewed claim must not be taken over");

        clock.Advance(TimeSpan.FromSeconds(45)); // the holder stopped renewing: its lease runs out
        var takeover = await inbox.TryClaimAsync(Consumer, messageId, lease, ct);
        Assert.AreEqual(InboxClaimStatus.Acquired, takeover.Status);
        Assert.IsFalse(await inbox.RenewAsync(Consumer, messageId, holder.ClaimToken, lease, ct),
            "the old holder must learn its claim is gone");

        Assert.IsTrue(await inbox.CompleteAsync(Consumer, messageId, takeover.ClaimToken, ct));
        Assert.IsFalse(await inbox.RenewAsync(Consumer, messageId, takeover.ClaimToken, lease, ct),
            "a completed claim is never renewed");
    }

    /// <summary>Retention removes completed and abandoned claims older than the cutoff and keeps everything else.</summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Purge_RemovesOldCompletedAndAbandonedClaims_KeepsLiveAndRecent()
    {
        var ct = TestContext.CancellationToken;
        var connString = await MigratedDatabaseAsync("inboxpurge", ct);
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        var start = DateTimeOffset.UtcNow.AddDays(-10);
        var clock = new MutableClock(start);
        var inbox = Store(db, connString, clock);

        var oldCompleted = Guid.CreateVersion7();
        var claim = await inbox.TryClaimAsync(Consumer, oldCompleted, Lease, ct);
        await inbox.CompleteAsync(Consumer, oldCompleted, claim.ClaimToken, ct);
        var oldAbandoned = Guid.CreateVersion7();
        await inbox.TryClaimAsync(Consumer, oldAbandoned, Lease, ct);

        clock.Advance(TimeSpan.FromDays(10));
        var recentCompleted = Guid.CreateVersion7();
        claim = await inbox.TryClaimAsync(Consumer, recentCompleted, Lease, ct);
        await inbox.CompleteAsync(Consumer, recentCompleted, claim.ClaimToken, ct);
        var live = Guid.CreateVersion7();
        await inbox.TryClaimAsync(Consumer, live, Lease, ct);

        var purged = await inbox.PurgeAsync(start.AddDays(7), ct);

        Assert.AreEqual(2, purged);
        var remaining = await db.ConsumerInbox.AsNoTracking().Select(x => x.MessageId).ToListAsync(ct);
        CollectionAssert.AreEquivalent(new[] { recentCompleted, live }, remaining);
    }

    /// <summary>
    /// The migration backfills claims written by the one-state inbox as completed, each with its own token, so a
    /// redelivery of an already-processed message stays a duplicate after the upgrade.
    /// </summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Migration_BackfillsPreExistingClaimsAsCompleted()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("inboxbackfill");
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        var migrations = db.Database.GetMigrations().ToList();
        var twoState = migrations.FindIndex(m => m.EndsWith("_TwoStateInboxAndOutboxTraceContext", StringComparison.Ordinal));
        Assert.IsGreaterThan(0, twoState, "the two-state inbox migration must exist and not be the first");

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[twoState - 1], cancellationToken: ct);
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        await db.Database.ExecuteSqlAsync(
            $"""INSERT INTO taskflow."ConsumerInbox" ("Consumer", "MessageId", "ProcessedAtUtc") VALUES ({Consumer}, {first}, {DateTimeOffset.UtcNow})""", ct);
        await db.Database.ExecuteSqlAsync(
            $"""INSERT INTO taskflow."ConsumerInbox" ("Consumer", "MessageId", "ProcessedAtUtc") VALUES ({Consumer}, {second}, {DateTimeOffset.UtcNow})""", ct);

        await migrator.MigrateAsync(cancellationToken: ct);

        var rows = await db.ConsumerInbox.AsNoTracking().ToListAsync(ct);
        Assert.HasCount(2, rows);
        Assert.IsTrue(rows.All(r => r.CompletedAtUtc == r.ClaimedAtUtc), "every pre-existing claim is completed");
        Assert.AreEqual(2, rows.Select(r => r.ClaimToken).Where(t => t != Guid.Empty).Distinct().Count(),
            "each row needs its own token: retention batches on it");
        Assert.AreEqual(InboxClaimStatus.Duplicate, (await Store(db, connString).TryClaimAsync(Consumer, first, Lease, ct)).Status);
    }

    /// <summary>
    /// RabbitMQ crash semantics end to end on the real store: the holder died holding the claim and the broker
    /// redelivered at once. The consumer waits the lease out, takes the claim over and runs the effect once.
    /// </summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Consumer_AfterAHolderCrash_TakesTheClaimOverAndRunsOnce()
    {
        var ct = TestContext.CancellationToken;
        var connString = await MigratedDatabaseAsync("inboxcrash", ct);
        var envelope = Envelope();
        await using (var crashedDb = DbContainerFixture.CreateTrxnContext(connString))
        {
            var crashed = await Store(crashedDb, connString).TryClaimAsync(ProbeConsumer.Name, envelope.Id, ScaledClaim.ClaimLease, ct);
            Assert.AreEqual(InboxClaimStatus.Acquired, crashed.Status);
        }

        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        var redelivery = new ProbeConsumer(Store(db, connString));
        await redelivery.HandleAsync(envelope, ct);

        Assert.AreEqual(1, redelivery.Consumed);
        var row = await db.ConsumerInbox.AsNoTracking().SingleAsync(x => x.MessageId == envelope.Id, ct);
        Assert.IsNotNull(row.CompletedAtUtc);
    }

    /// <summary>
    /// A holder whose handler runs well past the lease renews its claim on the real store, so a concurrent
    /// redelivery waits the whole bound and is sent back for retry instead of taking the claim over and running
    /// the effect a second time.
    /// </summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Consumer_WithALongHandler_RenewsSoAConcurrentDeliveryCannotTakeOver()
    {
        var ct = TestContext.CancellationToken;
        var connString = await MigratedDatabaseAsync("inboxlong", ct);
        var envelope = Envelope();
        var claimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var holderDb = DbContainerFixture.CreateTrxnContext(connString);
        var holder = new ProbeConsumer(Store(holderDb, connString))
        {
            // Models slow work: two and a half leases, so an unrenewed claim would expire mid-run.
            Work = async token =>
            {
                claimed.TrySetResult();
                await Task.Delay(ScaledClaim.ClaimLease * 2.5, token);
            }
        };

        var running = holder.HandleAsync(envelope, ct);
        await claimed.Task.WaitAsync(TimeSpan.FromSeconds(30), ct);
        await using var waiterDb = DbContainerFixture.CreateTrxnContext(connString);
        var waiter = new ProbeConsumer(Store(waiterDb, connString));

        await Assert.ThrowsExactlyAsync<InboxClaimInProgressException>(() => waiter.HandleAsync(envelope, ct));
        await running;

        Assert.AreEqual(1, holder.Consumed);
        Assert.AreEqual(0, waiter.Consumed, "the effect must not run a second time");
        var row = await holderDb.ConsumerInbox.AsNoTracking().SingleAsync(x => x.MessageId == envelope.Id, ct);
        Assert.IsNotNull(row.CompletedAtUtc);
    }

    public TestContext TestContext { get; set; } = null!;

    /// <summary>The default timings scaled down one-sixtieth so the waits stay short.</summary>
    private static readonly InboxClaimOptions ScaledClaim = new()
    {
        ClaimLease = TimeSpan.FromSeconds(1),
        PollInterval = TimeSpan.FromMilliseconds(50),
        WaitMargin = TimeSpan.FromMilliseconds(250)
    };

    private static IntegrationEventEnvelope Envelope() => TaskFlowIntegrationEvents.Envelope(
        new TaskItemCreatedEvent(Guid.CreateVersion7(), Guid.NewGuid(), "inbox"), DateTimeOffset.UtcNow, correlationId: null);

    private sealed class ProbeConsumer(IInboxStore inbox)
        : IntegrationEventConsumer(inbox, new MessagingMetrics(), NullLogger.Instance, Options.Create(ScaledClaim))
    {
        public const string Name = "probe";

        private int _consumed;

        public int Consumed => Volatile.Read(ref _consumed);

        public Func<CancellationToken, Task>? Work { get; init; }

        public override string ConsumerName => Name;

        public override bool Handles(string eventType) => true;

        protected override async Task ConsumeAsync(IntegrationEventEnvelope envelope, CancellationToken ct)
        {
            if (Work is not null) await Work(ct);
            Interlocked.Increment(ref _consumed);
        }
    }

    private static async Task<string> MigratedDatabaseAsync(string prefix, CancellationToken ct)
    {
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync(prefix);
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        await db.Database.MigrateAsync(ct);
        return connString;
    }

    private static InboxStore Store(TaskFlowDbContextTrxn db, string connString, TimeProvider? clock = null) =>
        new(db, new ContainerContextFactory(connString), clock);

    /// <summary>Fresh contexts for renewal, the way the pooled factory hands them out in the hosts.</summary>
    private sealed class ContainerContextFactory(string connString) : IDbContextFactory<TaskFlowDbContextTrxn>
    {
        public TaskFlowDbContextTrxn CreateDbContext() => DbContainerFixture.CreateTrxnContext(connString);
    }

    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
