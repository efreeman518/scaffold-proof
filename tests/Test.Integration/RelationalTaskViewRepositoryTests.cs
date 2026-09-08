using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// D-038 relational read model against a real database, on whichever provider the lane selected
/// (TASKFLOW_TEST_DB_PROVIDER). Everything asserted here is provider-generated SQL that cannot be reasoned
/// about from the C#: the FlexLabs upsert (MERGE / ON CONFLICT), the server-side counter deltas that keep
/// two concurrent projection events from overwriting each other, and the keyset page whose WHERE clause has
/// to agree with its ORDER BY or a client silently loses rows.
/// Component tier: contexts directly against the standalone database Testcontainer.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class RelationalTaskViewRepositoryTests
{
    private const int ConcurrentPatchers = 8;

    /// <summary>Marks the test Inconclusive when the database container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("SQL", DbContainerFixture.StartupError);

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task UpsertThenGet_RoundTripsEveryProjectedField_AndReplacesOnSecondUpsert()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("taskview");
        await MigrateAsync(connString, ct);

        var tenantId = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        var dto = NewDto(tenantId, id, "first", DateTimeOffset.UtcNow);
        dto.Description = "a description nothing indexes";
        dto.Tags = ["alpha", "beta"];
        dto.CommentCount = 3;
        dto.ChecklistTotal = 4;
        dto.ChecklistCompleted = 2;
        dto.AttachmentCount = 1;
        dto.SubTaskCount = 5;
        dto.CategoryName = "Work";
        // Whole-second literal on purpose: PostgreSQL timestamptz stores microseconds while SQL Server
        // datetimeoffset stores 100ns, so a value carrying sub-microsecond ticks cannot round-trip
        // bit-identically on both providers and would make this an assertion about the provider, not the
        // repository. The continuation token sidesteps the same problem by always being minted from the
        // value the database returned.
        dto.DueDate = new DateTimeOffset(2026, 9, 11, 22, 27, 56, TimeSpan.Zero);

        await using (var repoScope = NewRepository(connString))
        {
            await repoScope.Repository.UpsertAsync(dto, ct);
        }

        await using (var repoScope = NewRepository(connString))
        {
            var loaded = await repoScope.Repository.GetAsync(id, tenantId, ct);

            Assert.IsNotNull(loaded);
            Assert.AreEqual("first", loaded.Title);
            Assert.AreEqual(dto.Status, loaded.Status);
            Assert.AreEqual(dto.Priority, loaded.Priority);
            Assert.AreEqual("Work", loaded.CategoryName);
            // Description and tags live in the JSON body column, so a round trip proves the serializer too.
            Assert.AreEqual(dto.Description, loaded.Description);
            CollectionAssert.AreEqual(dto.Tags, loaded.Tags);
            Assert.AreEqual(3, loaded.CommentCount);
            Assert.AreEqual(4, loaded.ChecklistTotal);
            Assert.AreEqual(2, loaded.ChecklistCompleted);
            Assert.AreEqual(1, loaded.AttachmentCount);
            Assert.AreEqual(5, loaded.SubTaskCount);
            Assert.AreEqual(dto.DueDate, loaded.DueDate);
        }

        // Second upsert of the same key replaces the row rather than inserting or failing: the projection
        // rebuilds the whole document, exactly as Cosmos UpsertItem does.
        var replacement = NewDto(tenantId, id, "second", dto.LastModifiedUtc.AddMinutes(1));
        await using (var repoScope = NewRepository(connString))
        {
            await repoScope.Repository.UpsertAsync(replacement, ct);
        }

        await using (var verify = DbContainerFixture.CreateQueryContext(connString))
        {
            var rows = await verify.TaskViews.AsNoTracking().Where(e => e.TenantId == tenantId).ToListAsync(ct);
            Assert.AreEqual(1, rows.Count, "the upsert must replace, not duplicate");
            Assert.AreEqual("second", rows[0].Title);
            Assert.AreEqual(0, rows[0].CommentCount, "a replace clears counters the new projection did not set");
        }

        // A missing document reads as null, not as an empty projection.
        await using (var repoScope = NewRepository(connString))
        {
            Assert.IsNull(await repoScope.Repository.GetAsync(Guid.NewGuid().ToString(), tenantId, ct));
        }
    }

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task ConcurrentPatchCounters_LoseNoDeltas_AndAMissingRowIsANoOp()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("taskviewpatch");
        await MigrateAsync(connString, ct);

        var tenantId = Guid.NewGuid().ToString();
        var id = Guid.NewGuid().ToString();
        var createdUtc = DateTimeOffset.UtcNow;

        await using (var repoScope = NewRepository(connString))
        {
            await repoScope.Repository.UpsertAsync(NewDto(tenantId, id, "counters", createdUtc), ct);
        }

        // Eight replicas applying one delta each at the same time. A read-modify-write would lose all but
        // one; a server-side `SET x = x + @d` cannot.
        await Task.WhenAll(Enumerable.Range(0, ConcurrentPatchers).Select(i => Task.Run(async () =>
        {
            await using var repoScope = NewRepository(connString);
            await repoScope.Repository.PatchCountersAsync(
                id, tenantId,
                new Dictionary<string, int>(StringComparer.Ordinal)
                {
                    ["commentCount"] = 1,
                    ["attachmentCount"] = 0,
                    ["checklistTotal"] = 2,
                    ["checklistCompleted"] = -1
                },
                createdUtc.AddSeconds(i + 1),
                ct);
        }, ct)));

        await using (var verify = DbContainerFixture.CreateQueryContext(connString))
        {
            var row = await verify.TaskViews.AsNoTracking().SingleAsync(e => e.Id == id, ct);
            Assert.AreEqual(ConcurrentPatchers, row.CommentCount);
            Assert.AreEqual(2 * ConcurrentPatchers, row.ChecklistTotal);
            Assert.AreEqual(-ConcurrentPatchers, row.ChecklistCompleted, "negative deltas apply the same way");
            Assert.AreEqual(0, row.AttachmentCount, "a zero delta must not appear in the UPDATE at all");
            Assert.IsGreaterThan(createdUtc, row.LastModifiedUtc);
        }

        // A row the create projection has not written yet: ignored, never inserted. The create projection
        // will compute the counters from the source.
        await using (var repoScope = NewRepository(connString))
        {
            await repoScope.Repository.PatchCountersAsync(
                Guid.NewGuid().ToString(), tenantId,
                new Dictionary<string, int>(StringComparer.Ordinal) { ["commentCount"] = 1 },
                DateTimeOffset.UtcNow, ct);
        }

        await using (var verify = DbContainerFixture.CreateQueryContext(connString))
        {
            Assert.AreEqual(1, await verify.TaskViews.CountAsync(e => e.TenantId == tenantId, ct),
                "patching a missing row must not create one");
        }

        // Delete is idempotent for the same reason.
        await using (var repoScope = NewRepository(connString))
        {
            await repoScope.Repository.DeleteAsync(id, tenantId, ct);
            await repoScope.Repository.DeleteAsync(id, tenantId, ct);
        }

        await using (var verify = DbContainerFixture.CreateQueryContext(connString))
        {
            Assert.AreEqual(0, await verify.TaskViews.CountAsync(e => e.TenantId == tenantId, ct));
        }
    }

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task KeysetPaging_WalksEveryRowOnce_AndRejectsAnotherTenantsToken()
    {
        const int rowCount = 25;
        const int pageSize = 10;

        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("taskviewpage");
        await MigrateAsync(connString, ct);

        var tenantId = Guid.NewGuid().ToString();
        var otherTenantId = Guid.NewGuid().ToString();
        var baseUtc = DateTimeOffset.UtcNow.AddHours(-1);

        await using (var repoScope = NewRepository(connString))
        {
            for (var i = 0; i < rowCount; i++)
            {
                // Deliberate timestamp ties (three rows per minute) so the Id tie-break is exercised: with
                // only the timestamp in the WHERE clause a tie would repeat or skip rows across pages.
                await repoScope.Repository.UpsertAsync(
                    NewDto(tenantId, $"row-{i:D2}-{Guid.NewGuid()}", $"row {i}", baseUtc.AddMinutes(i / 3)), ct);
            }

            await repoScope.Repository.UpsertAsync(
                NewDto(otherTenantId, Guid.NewGuid().ToString(), "other tenant", baseUtc), ct);
        }

        var seen = new List<string>();
        var lastModified = new List<DateTimeOffset>();
        string? token = null;
        var pages = 0;

        do
        {
            await using var repoScope = NewRepository(connString);
            var page = await repoScope.Repository.QueryByTenantAsync(tenantId, pageSize, token, ct);
            pages++;

            Assert.IsLessThanOrEqualTo(pageSize, page.Items.Count);
            seen.AddRange(page.Items.Select(i => i.Id));
            lastModified.AddRange(page.Items.Select(i => i.LastModifiedUtc));
            token = page.ContinuationToken;
        }
        while (token is not null);

        Assert.AreEqual(3, pages, "25 rows at 10 per page is three pages");
        Assert.IsNull(token, "the final page must return no token, or a client loops forever");
        Assert.AreEqual(rowCount, seen.Count, "no gaps");
        Assert.AreEqual(rowCount, seen.Distinct(StringComparer.Ordinal).Count(), "no repeats");
        CollectionAssert.AreEqual(
            lastModified.OrderByDescending(v => v).ToList(), lastModified,
            "pages must stay in the declared LastModifiedUtc descending order");

        // The other tenant's row never entered the result set (the counts above), and its token is not
        // usable here: a token is bound to the tenant it was minted for.
        await using (var repoScope = NewRepository(connString))
        {
            var otherPage = await repoScope.Repository.QueryByTenantAsync(otherTenantId, 1, null, ct);
            Assert.AreEqual(1, otherPage.Items.Count);

            var foreignToken = TaskViewKeysetToken.Encode(otherTenantId, baseUtc, otherPage.Items[0].Id);
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => repoScope.Repository.QueryByTenantAsync(tenantId, pageSize, foreignToken, ct));
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task MigrateAsync(string connString, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        await db.Database.MigrateAsync(ct);
    }

    /// <summary>
    /// A repository over its own context pair, so a test can hold several at once the way concurrent
    /// requests do. The write context is Trxn and the read context is Query (D-027); both point at the same
    /// database here, which is what a single-node deployment looks like.
    /// </summary>
    private static RepositoryScope NewRepository(string connString)
    {
        var write = DbContainerFixture.CreateTrxnContext(connString);
        var read = DbContainerFixture.CreateQueryContext(connString);
        return new RepositoryScope(write, read, new RelationalTaskViewRepository(write, read));
    }

    private sealed record RepositoryScope(
        TaskFlowDbContextTrxn Write,
        TaskFlowDbContextQuery Read,
        ITaskViewRepository Repository) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await Write.DisposeAsync();
            await Read.DisposeAsync();
        }
    }

    private static TaskViewDto NewDto(string tenantId, string id, string title, DateTimeOffset lastModifiedUtc) =>
        new()
        {
            Id = id,
            TenantId = tenantId,
            Title = title,
            Status = "InProgress",
            Priority = "High",
            CreatedUtc = lastModifiedUtc.AddDays(-1),
            LastModifiedUtc = lastModifiedUtc
        };
}
