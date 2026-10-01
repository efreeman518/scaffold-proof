using EF.Data.Contracts;
using EF.Data.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data;
using Test.Integration.Infrastructure;
using Test.Support;

namespace Test.Integration;

/// <summary>
/// D-026 claim semantics (EF.Data.Outbox LeasedWorkStore over TaskFlow's context and migrations) against a real
/// database, on whichever provider the lane selected. The claim is the
/// only thing stopping two Scheduler replicas from dispatching the same event twice, and it is expressed in
/// provider-neutral EF Core, so it has to be proven on both providers rather than reasoned about.
/// Component tier: contexts directly against the standalone database Testcontainer.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class OutboxClaimTests
{
    private const int RowCount = 1000;
    private const int ClaimerCount = 4;
    private const int MaxAttempts = 10;
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(5);

    /// <summary>Inconclusive without a container runtime; fails when the database container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("SQL", DbContainerFixture.StartupError);

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task ConcurrentClaimers_NeverOverlap_AndDrainEveryRow()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("outboxclaim", ct);
        await MigrateAsync(connString, ct);
        await SeedOutboxAsync(connString, RowCount, ct);

        // Four replicas claiming at once, exactly as two Scheduler replicas with two workers each would.
        var claims = await Task.WhenAll(Enumerable.Range(0, ClaimerCount)
            .Select(i => Task.Run(() => DrainAsync(connString, $"claimer-{i}", ct), ct)));

        var all = claims.SelectMany(c => c).ToList();
        var byRow = all.GroupBy(c => c.RowId).ToList();

        Assert.AreEqual(RowCount, byRow.Count, "every seeded row must be claimed exactly once");
        var overlapping = byRow.Where(g => g.Select(c => c.LeaseToken).Distinct().Count() > 1).ToList();
        Assert.AreEqual(0, overlapping.Count,
            $"rows claimed under more than one lease token: {string.Join(", ", overlapping.Take(5).Select(g => g.Key))}");

        await using var verify = DbContainerFixture.CreateTrxnContext(connString);
        Assert.AreEqual(0, await verify.OutboxMessages.CountAsync(ct), "successful dispatch hard-deletes the row");
    }

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task ExpiredLease_IsReclaimable_AndDeadLetteredRowSurvives()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("outboxlease", ct);
        await MigrateAsync(connString, ct);
        await SeedOutboxAsync(connString, 1, ct);

        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var work = Store(db, clock);

        // A replica claims and then dies without releasing: the lease must expire, not park the row forever.
        var abandoned = await work.ClaimAsync<OutboxMessage>(new LeaseRequest(10, Lease, MaxAttempts, "dead-replica"), ct);
        Assert.AreEqual(1, abandoned.Items.Count);
        Assert.AreEqual(1, abandoned.Items[0].AttemptCount);
        Assert.AreEqual(0, (await work.ClaimAsync<OutboxMessage>(new LeaseRequest(10, Lease, MaxAttempts, "live-replica"), ct)).Items.Count,
            "a live lease is not claimable");

        clock.Advance(Lease + TimeSpan.FromSeconds(1));
        var reclaimed = await work.ClaimAsync<OutboxMessage>(new LeaseRequest(10, Lease, MaxAttempts, "live-replica"), ct);
        Assert.AreEqual(1, reclaimed.Items.Count);
        Assert.AreNotEqual(abandoned.LeaseToken, reclaimed.LeaseToken);
        Assert.AreEqual(2, reclaimed.Items[0].AttemptCount, "each claim counts as an attempt");

        // Dead-lettered, the row is parked, not deleted: it is the only surviving copy of the event.
        var row = reclaimed.Items[0];
        Assert.IsTrue(await work.DeadLetterAsync<OutboxMessage>(reclaimed.LeaseToken, row.Id, "poison", ct));

        await using var verify = DbContainerFixture.CreateTrxnContext(connString);
        var parked = await verify.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == row.Id, ct);
        Assert.IsNotNull(parked.DeadLetteredAtUtc);
        Assert.AreEqual("poison", parked.LastError);
        Assert.IsNull(parked.LeaseToken);

        var afterDeadLetter = await work.ClaimAsync<OutboxMessage>(new LeaseRequest(10, Lease, MaxAttempts, "live-replica"), ct);
        Assert.AreEqual(0, afterDeadLetter.Items.Count, "a dead-lettered row is never claimed again");

        // The admin retry endpoint puts it back in play.
        Assert.IsTrue(await work.RetryDeadLetteredAsync<OutboxMessage>(row.Id, ct));
        var afterRetry = await work.ClaimAsync<OutboxMessage>(new LeaseRequest(10, Lease, MaxAttempts, "live-replica"), ct);
        Assert.AreEqual(1, afterRetry.Items.Count);
    }

    /// <summary>
    /// A row whose last attempt never settled - the Scheduler crashed, or the send hung past the lease - is parked
    /// on the next claim once its lease expires, instead of being re-leased and re-sent forever.
    /// </summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task ExhaustedRowWithExpiredLease_IsDeadLetteredOnTheNextClaim()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("outboxpoison", ct);
        await MigrateAsync(connString, ct);
        await SeedOutboxAsync(connString, 1, ct);

        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var work = Store(db, clock);
        const int maxAttempts = 3;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var claim = await work.ClaimAsync<OutboxMessage>(new LeaseRequest(10, Lease, maxAttempts, "crashing-replica"), ct);
            Assert.AreEqual(1, claim.Items.Count, $"attempt {attempt} should still claim the row");
            Assert.AreEqual(attempt, claim.Items[0].AttemptCount);
            clock.Advance(Lease + TimeSpan.FromSeconds(1)); // the replica dies holding the lease
        }

        var afterLast = await work.ClaimAsync<OutboxMessage>(new LeaseRequest(10, Lease, maxAttempts, "live-replica"), ct);
        Assert.AreEqual(0, afterLast.Items.Count, "an exhausted row must not be leased a fourth time");

        await using var verify = DbContainerFixture.CreateTrxnContext(connString);
        var parked = await verify.OutboxMessages.AsNoTracking().SingleAsync(ct);
        Assert.IsNotNull(parked.DeadLetteredAtUtc, "the exhausted row is parked, and kept");
        Assert.AreEqual("Lease expired on final attempt", parked.LastError);
        Assert.IsNull(parked.LeaseToken);
        Assert.AreEqual(maxAttempts, parked.AttemptCount);
    }

    /// <summary>
    /// The attempt ceiling comes from the caller (the worker's MaxAttempts option), not a constant: a row at
    /// attempt 2 is claimed under a ceiling of 3, and under a ceiling of 2 it is parked instead.
    /// </summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Claim_HonorsTheCallersAttemptCeiling()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("outboxceiling", ct);
        await MigrateAsync(connString, ct);
        await SeedOutboxAsync(connString, 1, ct);

        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        await db.OutboxMessages.ExecuteUpdateAsync(s => s.SetProperty(m => m.AttemptCount, 2), ct);
        var work = Store(db);

        var underThree = await work.ClaimAsync<OutboxMessage>(new LeaseRequest(10, Lease, 3, "replica"), ct);
        Assert.AreEqual(1, underThree.Items.Count, "attempt 3 of 3 is still allowed");
        Assert.AreEqual(3, underThree.Items[0].AttemptCount);
        Assert.AreEqual(1, await work.AbandonAsync<OutboxMessage>(underThree.LeaseToken, [underThree.Items[0].Id], ct));

        var underTwo = await work.ClaimAsync<OutboxMessage>(new LeaseRequest(10, Lease, 2, "replica"), ct);
        Assert.AreEqual(0, underTwo.Items.Count, "two attempts are spent under a ceiling of 2");

        await using var verify = DbContainerFixture.CreateTrxnContext(connString);
        var parked = await verify.OutboxMessages.AsNoTracking().SingleAsync(ct);
        Assert.IsNotNull(parked.DeadLetteredAtUtc, "the exhausted row is parked by the claim, not left live");
    }

    /// <summary>
    /// Shutdown hands claimed rows back immediately and without spending an attempt; a settlement under a token
    /// that no longer owns the rows changes nothing.
    /// </summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Abandon_RestoresTheAttempt_AndForeignTokensSettleNothing()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("outboxabandon", ct);
        await MigrateAsync(connString, ct);
        await SeedOutboxAsync(connString, 1, ct);

        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        var work = Store(db);
        var claim = await work.ClaimAsync<OutboxMessage>(new LeaseRequest(10, Lease, MaxAttempts, "stopping-replica"), ct);
        var id = claim.Items[0].Id;
        var foreign = Guid.CreateVersion7();

        Assert.AreEqual(0, await work.CompleteAsync<OutboxMessage>(foreign, [id], ct));
        Assert.IsFalse(await work.ReleaseAsync<OutboxMessage>(foreign, id, TimeSpan.FromSeconds(2), "x", ct));
        Assert.IsFalse(await work.DeadLetterAsync<OutboxMessage>(foreign, id, "x", ct));
        Assert.AreEqual(0, await work.AbandonAsync<OutboxMessage>(foreign, [id], ct));

        Assert.AreEqual(1, await work.AbandonAsync<OutboxMessage>(claim.LeaseToken, [id], ct));
        var reclaimed = await work.ClaimAsync<OutboxMessage>(new LeaseRequest(10, Lease, MaxAttempts, "next-replica"), ct);
        Assert.AreEqual(1, reclaimed.Items.Count, "an abandoned row is claimable at once, without waiting out the lease");
        Assert.AreEqual(1, reclaimed.Items[0].AttemptCount, "the abandoned claim did not consume an attempt");
    }

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task RolledBackSave_LeavesNoOutboxRow()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("outboxtrx", ct);
        await MigrateAsync(connString, ct);

        await using var db = DbContainerFixture.CreateTrxnContext(connString);

        // The retrying execution strategy refuses a user-initiated transaction unless the whole unit runs
        // through it; that is the same rule production code follows.
        await db.Database.CreateExecutionStrategy().ExecuteAsync(async () =>
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            var task = TaskItem.Create(TenantId.From(TestConstants.TenantId), "rolled back").Value!;
            db.TaskItems.Add(task);
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);

            // The row exists inside the transaction: staging really did join this unit of work.
            Assert.AreEqual(1, await db.OutboxMessages.CountAsync(ct));
            await transaction.RollbackAsync(ct);
        });

        await using var verify = DbContainerFixture.CreateTrxnContext(connString);
        Assert.AreEqual(0, await verify.OutboxMessages.CountAsync(ct),
            "an event may not outlive the domain write it describes");
        Assert.AreEqual(0, await verify.TaskItems.IgnoreQueryFilters().CountAsync(ct));
    }

    /// <summary>
    /// The migration to the package outbox shape carries every row's tenant into the TenantId header before it
    /// drops the column, so a row staged before the upgrade is still published with its tenant.
    /// </summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Migration_CarriesThePreExistingTenantIntoTheHeaders()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("outboxheaders", ct);
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        var migrations = db.Database.GetMigrations().ToList();
        var packageShape = migrations.FindIndex(m => m.EndsWith("_PackageOutboxMessage", StringComparison.Ordinal));
        Assert.IsGreaterThan(0, packageShape, "the package outbox migration must exist and not be the first");

        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync(migrations[packageShape - 1], cancellationToken: ct);
        var id = Guid.CreateVersion7();
        var tenantId = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO taskflow."OutboxMessage" ("Id", "TenantId", "AvailableAtUtc", "AttemptCount", "Destination", "EventType", "EventVersion", "Payload", "OccurredAtUtc")
            VALUES ({id}, {tenantId}, {now}, 0, {TaskFlowIntegrationEvents.Destination}, {"TaskItemCreatedEvent"}, 1, {"{}"}, {now})
            """, ct);

        await migrator.MigrateAsync(cancellationToken: ct);

        var row = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == id, ct);
        var headers = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(row.Headers!);
        Assert.AreEqual(tenantId.ToString(), headers![TaskFlowIntegrationEvents.TenantIdHeader],
            "the header must hold the tenant in the lowercase form Guid.ToString() writes for new rows");
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task MigrateAsync(string connString, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        await db.Database.MigrateAsync(ct);
    }

    private static async Task SeedOutboxAsync(string connString, int count, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        var now = DateTimeOffset.UtcNow.AddMinutes(-1);
        for (var i = 0; i < count; i++)
        {
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.CreateVersion7(),
                AvailableAtUtc = now,
                Destination = TaskFlowIntegrationEvents.Destination,
                EventType = "TaskItemCreatedEvent",
                EventVersion = 1,
                Payload = "{}",
                OccurredAtUtc = now
            });
        }

        await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);
    }

    private static async Task<List<(Guid RowId, Guid LeaseToken)>> DrainAsync(
        string connString, string owner, CancellationToken ct)
    {
        var claimed = new List<(Guid, Guid)>();

        while (true)
        {
            await using var db = DbContainerFixture.CreateTrxnContext(connString);
            var work = Store(db);
            var batch = await work.ClaimAsync<OutboxMessage>(new LeaseRequest(25, Lease, MaxAttempts, owner), ct);
            if (batch.Items.Count == 0) break;

            foreach (var item in batch.Items)
            {
                claimed.Add((item.Id, batch.LeaseToken));
                Assert.AreEqual(batch.LeaseToken, item.LeaseToken, "read-back must key on the lease token");
                Assert.AreEqual(owner, item.LeaseOwner);
            }

            var deleted = await work.CompleteAsync<OutboxMessage>(
                batch.LeaseToken, [.. batch.Items.Select(i => i.Id)], ct);
            Assert.AreEqual(batch.Items.Count, deleted);
        }

        return claimed;
    }

    private static LeasedWorkStore<TaskFlowDbContextTrxn> Store(TaskFlowDbContextTrxn db, TimeProvider? clock = null) =>
        new(db, clock);

    /// <summary>Clock a test moves forward to expire leases, instead of claiming with a negative lease.</summary>
    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
