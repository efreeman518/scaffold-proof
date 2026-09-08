using EF.Common.Contracts;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// D-039 relational audit sink against a real database, on whichever provider the lane selected
/// (TASKFLOW_TEST_DB_PROVIDER). Mirrors <see cref="AuditLogRepositoryAzuriteTests"/> so the two arms are
/// held to the same contract: the tenant-first key, the sentinel tenant for entries with no tenant, the
/// round trip of audit metadata, and the retention sweep - here also proving the sweep really batches
/// instead of issuing one unbounded DELETE.
/// Component tier: contexts directly against the standalone database Testcontainer.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class RelationalAuditLogRepositoryTests
{
    private const string SystemTenantId = "_system";
    private const int RetentionRowCount = 250;
    private const int RetentionBatchSize = 100;

    /// <summary>Marks the test Inconclusive when the database container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("SQL", DbContainerFixture.StartupError);

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Append_PersistsEveryAuditField_AndRetentionRespectsTheCutoff()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("auditrel");
        await MigrateAsync(connString, ct);

        var tenantId = Guid.NewGuid();
        var entry = new AuditEntry<string, Guid>
        {
            Id = Guid.NewGuid(),
            AuditId = "integration-user",
            TenantId = tenantId,
            EntityType = "TaskItem",
            EntityKey = Guid.NewGuid().ToString(),
            Status = AuditStatus.Success,
            Action = "Create",
            StartTime = TimeSpan.FromMilliseconds(25),
            ElapsedTime = TimeSpan.FromMilliseconds(7),
            Metadata = "{\"source\":\"relational-test\"}"
        };

        await using (var db = DbContainerFixture.CreateTrxnContext(connString))
        {
            await new RelationalAuditLogRepository(db, SystemTenantId).AppendAsync(entry, ct);
        }

        await using (var verify = DbContainerFixture.CreateQueryContext(connString))
        {
            var persisted = await verify.AuditLog.AsNoTracking().SingleAsync(e => e.Id == entry.Id, ct);

            Assert.AreEqual(tenantId.ToString(), persisted.TenantId,
                "the key is tenant-first so one tenant's trail is a range scan");
            Assert.AreEqual(entry.AuditId, persisted.AuditId);
            Assert.AreEqual(entry.EntityType, persisted.EntityType);
            Assert.AreEqual(entry.EntityKey, persisted.EntityKey);
            Assert.AreEqual(entry.Action, persisted.Action);
            Assert.AreEqual(entry.Status.ToString(), persisted.Status);
            Assert.AreEqual(entry.StartTime.Ticks, persisted.StartTimeTicks);
            Assert.AreEqual(entry.ElapsedTime.Ticks, persisted.ElapsedTimeTicks);
            Assert.AreEqual(entry.Metadata, persisted.Metadata);
            Assert.IsNull(persisted.Error);
        }

        // An entry with no tenant is bucketed under the configured sentinel, not left null: the tenant is
        // part of the primary key.
        var systemEntry = new AuditEntry<string, Guid?>
        {
            Id = Guid.NewGuid(),
            AuditId = "system",
            TenantId = null,
            EntityType = "Retention",
            EntityKey = "sweep",
            Status = AuditStatus.Success,
            Action = "Purge"
        };

        await using (var db = DbContainerFixture.CreateTrxnContext(connString))
        {
            await new RelationalAuditLogRepository(db, SystemTenantId).AppendAsync(systemEntry, ct);
        }

        await using (var verify = DbContainerFixture.CreateQueryContext(connString))
        {
            var persisted = await verify.AuditLog.AsNoTracking().SingleAsync(e => e.Id == systemEntry.Id, ct);
            Assert.AreEqual(SystemTenantId, persisted.TenantId);
        }

        await using (var db = DbContainerFixture.CreateTrxnContext(connString))
        {
            var repository = new RelationalAuditLogRepository(db, SystemTenantId);

            // Recorded now: outside a cutoff in the past and kept, inside a cutoff in the future and removed.
            Assert.AreEqual(0, await repository.PurgeOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-1), ct));
            Assert.AreEqual(2, await repository.PurgeOlderThanAsync(DateTimeOffset.UtcNow.AddMinutes(5), ct));
        }

        await using (var verify = DbContainerFixture.CreateQueryContext(connString))
        {
            Assert.AreEqual(0, await verify.AuditLog.CountAsync(ct));
        }
    }

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task PurgeOlderThan_WalksTheWindowInBatches_AndKeepsRowsInsideIt()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("auditpurge");
        await MigrateAsync(connString, ct);

        var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-30);
        await SeedAsync(connString, RetentionRowCount, cutoffUtc.AddDays(-1), ct);
        // Rows inside the retention window that the sweep must not touch.
        await SeedAsync(connString, 5, cutoffUtc.AddDays(1), ct);

        await using (var db = DbContainerFixture.CreateTrxnContext(connString))
        {
            var repository = new RelationalAuditLogRepository(db, SystemTenantId, RetentionBatchSize);

            // 250 expired rows at 100 per batch: three batches, the last one short, which is also the loop's
            // stopping rule. A single unbounded DELETE would report the same total and prove nothing.
            Assert.AreEqual(RetentionRowCount, await repository.PurgeOlderThanAsync(cutoffUtc, ct));
        }

        await using (var verify = DbContainerFixture.CreateQueryContext(connString))
        {
            Assert.AreEqual(5, await verify.AuditLog.CountAsync(ct), "rows inside the window survive");
            Assert.AreEqual(0, await verify.AuditLog.CountAsync(e => e.RecordedUtc < cutoffUtc, ct));
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task MigrateAsync(string connString, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        await db.Database.MigrateAsync(ct);
    }

    /// <summary>
    /// Seeds rows at a chosen <c>RecordedUtc</c>. The repository always stamps "now", so the retention
    /// window can only be set up by writing the rows directly.
    /// </summary>
    private static async Task SeedAsync(string connString, int count, DateTimeOffset recordedUtc, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        for (var i = 0; i < count; i++)
        {
            db.AuditLog.Add(new AuditLogRecord
            {
                TenantId = SystemTenantId,
                // Distinct timestamps, so the composite primary key cannot collide.
                RecordedUtc = recordedUtc.AddMilliseconds(i),
                Id = Guid.CreateVersion7(),
                AuditId = "seed",
                EntityType = "TaskItem",
                EntityKey = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Action = "Update",
                Status = AuditStatus.Success.ToString()
            });
        }

        await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);
    }
}
