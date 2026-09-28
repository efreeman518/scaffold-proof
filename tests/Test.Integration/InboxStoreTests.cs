using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Infrastructure.Repositories;
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
    private static readonly TimeSpan Lease = TimeSpan.FromMinutes(10);

    /// <summary>Marks the test Inconclusive when the database container failed to start.</summary>
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
            return await new InboxStore(db).TryClaimAsync(Consumer, messageId, Lease, ct);
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
        var inbox = new InboxStore(db);

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
        var inbox = new InboxStore(db, clock);
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
        var inbox = new InboxStore(db, clock);

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
        Assert.AreEqual(InboxClaimStatus.Duplicate, (await new InboxStore(db).TryClaimAsync(Consumer, first, Lease, ct)).Status);
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task<string> MigratedDatabaseAsync(string prefix, CancellationToken ct)
    {
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync(prefix);
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        await db.Database.MigrateAsync(ct);
        return connString;
    }

    private sealed class MutableClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan by) => _now += by;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}
