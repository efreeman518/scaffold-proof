using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;

using TaskFlow.Infrastructure.Data.Interceptors;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support;

namespace Test.Integration;

/// <summary>
/// D-026 claim semantics against a real database, on whichever provider the lane selected. The claim is the
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

    /// <summary>Marks the test Inconclusive when the database container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("SQL", DbContainerFixture.StartupError);

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task ConcurrentClaimers_NeverOverlap_AndDrainEveryRow()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("outboxclaim");
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
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("outboxlease");
        await MigrateAsync(connString, ct);
        await SeedOutboxAsync(connString, 1, ct);

        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        var work = new OperationalWorkRepository(db);

        // A replica claims and then dies without releasing: the lease must expire, not park the row forever.
        var abandoned = await work.ClaimAsync<OutboxMessage>(10, TimeSpan.FromMilliseconds(-1), "dead-replica", ct);
        Assert.AreEqual(1, abandoned.Items.Count);
        Assert.AreEqual(1, abandoned.Items[0].AttemptCount);

        var reclaimed = await work.ClaimAsync<OutboxMessage>(10, TimeSpan.FromMinutes(5), "live-replica", ct);
        Assert.AreEqual(1, reclaimed.Items.Count);
        Assert.AreNotEqual(abandoned.LeaseToken, reclaimed.LeaseToken);
        Assert.AreEqual(2, reclaimed.Items[0].AttemptCount, "each claim counts as an attempt");

        // Past the ceiling the row is parked, not deleted: it is the only surviving copy of the event.
        var row = reclaimed.Items[0];
        await work.ReleaseAsync<OutboxMessage>(
            reclaimed.LeaseToken, row.Id, OperationalWorkBase.MaxAttempts, "poison", ct);

        await using var verify = DbContainerFixture.CreateTrxnContext(connString);
        var parked = await verify.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == row.Id, ct);
        Assert.IsNotNull(parked.DeadLetteredAtUtc);
        Assert.AreEqual("poison", parked.LastError);
        Assert.IsNull(parked.LeaseToken);

        var afterDeadLetter = await work.ClaimAsync<OutboxMessage>(10, TimeSpan.FromMinutes(5), "live-replica", ct);
        Assert.AreEqual(0, afterDeadLetter.Items.Count, "a dead-lettered row is never claimed again");

        // The admin retry endpoint puts it back in play.
        Assert.IsTrue(await work.RetryDeadLetteredAsync<OutboxMessage>(row.Id, ct));
        var afterRetry = await work.ClaimAsync<OutboxMessage>(10, TimeSpan.FromMinutes(5), "live-replica", ct);
        Assert.AreEqual(1, afterRetry.Items.Count);
    }

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task RolledBackSave_LeavesNoOutboxRow()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("outboxtrx");
        await MigrateAsync(connString, ct);

        await using var db = DbContainerFixture.CreateTrxnContext(connString);

        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            var task = TaskItem.Create(DomainId.From<TenantId>(TestConstants.TenantId), "rolled back").Value!;
            db.TaskItems.Add(task);
            await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);

            // The row exists inside the transaction: staging really did join this unit of work.
            Assert.AreEqual(1, await db.OutboxMessages.CountAsync(ct));
            await transaction.RollbackAsync(ct);
        }

        await using var verify = DbContainerFixture.CreateTrxnContext(connString);
        Assert.AreEqual(0, await verify.OutboxMessages.CountAsync(ct),
            "an event may not outlive the domain write it describes");
        Assert.AreEqual(0, await verify.TaskItems.IgnoreQueryFilters().CountAsync(ct));
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
                TenantId = TestConstants.TenantId,
                AvailableAtUtc = now,
                Destination = OutboxStagingInterceptor.DefaultDestination,
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
            var work = new OperationalWorkRepository(db);
            var batch = await work.ClaimAsync<OutboxMessage>(25, TimeSpan.FromMinutes(5), owner, ct);
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
}
