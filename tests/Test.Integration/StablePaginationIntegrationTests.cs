using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support;
using Test.Support.Builders;

namespace Test.Integration;

/// <summary>
/// Proves keyset (cursor) paging walks every row exactly once against real SQL, including when the
/// leading sort key is duplicated across rows - the case an unstable sort silently corrupts.
/// Replaces the former offset-paging assertions: offset paging no longer exists on TaskItem search.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class StablePaginationIntegrationTests
{
    private static readonly Guid QueryTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>Ensures the shared SQL schema exists before pagination tests run.</summary>
    [ClassInitialize]
    public static async Task ClassInit(TestContext context)
    {
        if (IntegrationTestSetup.IsUnavailable(DbContainerFixture.StartupError))
            return;

        await using var db = DbContainerFixture.CreateTrxnContext();
        await db.Database.MigrateAsync(context.CancellationToken);
    }

    /// <summary>Marks the test inconclusive when the SQL container is unavailable.</summary>
    [TestInitialize]
    public void TestSetup() =>
        IntegrationTestSetup.AssertAvailable("SQL", DbContainerFixture.StartupError);

    /// <summary>Verifies the id-ordered keyset walk returns every row once with duplicate titles.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task IdAscKeyset_WithDuplicateTitles_ReturnsStableCompletePages()
    {
        await AssertKeysetWalkAsync(TaskItemSortMode.IdAsc);
    }

    /// <summary>Verifies the status keyset walk (duplicate leading key for every row) also stays total.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task StatusThenIdKeyset_WithDuplicateStatus_ReturnsStableCompletePages()
    {
        await AssertKeysetWalkAsync(TaskItemSortMode.StatusThenId);
    }

    /// <summary>Verifies the due-date ascending keyset walk stays total with a mix of null and set due dates.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task DueDateAscKeyset_WithNullAndSetDueDates_ReturnsStableCompletePages()
    {
        await AssertKeysetWalkAsync(TaskItemSortMode.DueDateAsc, mixedDueDates: true);
    }

    /// <summary>Verifies the due-date descending keyset walk stays total with a mix of null and set due dates.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task DueDateDescKeyset_WithNullAndSetDueDates_ReturnsStableCompletePages()
    {
        await AssertKeysetWalkAsync(TaskItemSortMode.DueDateDesc, mixedDueDates: true);
    }

    /// <summary>Verifies the modified-descending keyset walk stays total when every row shares a save timestamp.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task ModifiedDescKeyset_WithSharedTimestamps_ReturnsStableCompletePages()
    {
        await AssertKeysetWalkAsync(TaskItemSortMode.ModifiedDesc);
    }

    private async Task AssertKeysetWalkAsync(TaskItemSortMode sortMode, bool mixedDueDates = false)
    {
        var title = $"StablePaging-{Guid.NewGuid():N}";
        // Whole seconds: SQL Server keeps 100ns ticks, PostgreSQL timestamptz keeps microseconds.
        var dueBase = new DateTimeOffset(2027, 3, 1, 8, 0, 0, TimeSpan.Zero);
        var seeded = Enumerable.Range(0, 7)
            .Select(index =>
            {
                var task = new TaskItemBuilder()
                    .WithTenantId(QueryTenantId)
                    .WithTitle(title)
                    .Build();

                // Every second row keeps a null due date, and two rows share one due date, so the walk
                // crosses the null boundary and a duplicate leading key on the same page break.
                if (mixedDueDates && index % 2 == 0)
                    task.UpdateDateRange(null, dueBase.AddDays(index / 4));

                return task;
            })
            .ToArray();

        await using var writeDb = DbContainerFixture.CreateTrxnContext();
        writeDb.TaskItems.AddRange(seeded);
        await writeDb.SaveChangesAsync(
            OptimisticConcurrencyWinner.ClientWins,
            cancellationToken: TestContext.CancellationToken);

        try
        {
            var expected = seeded.Select(task => task.Id.Value).ToHashSet();

            await using var queryDb = DbContainerFixture.CreateQueryContext();
            var repository = new TaskItemRepositoryQuery(queryDb, TestColumnEncryption.Keys, TestCursorCodec.Instance);
            var actual = new List<Guid>();

            string? cursor = null;
            for (var page = 0; page < 5; page++)
            {
                var request = new TaskItemCursorSearchRequest
                {
                    Filter = new TaskItemSearchFilter { SearchTerm = title, TenantId = QueryTenantId },
                    SortMode = sortMode,
                    PageSize = 3,
                    Cursor = cursor
                };

                var result = await repository.SearchTaskItemsAsync(request, QueryTenantId, TestContext.CancellationToken);
                actual.AddRange(result.Items.Select(item => item.Id!.Value));

                if (!result.HasMore)
                {
                    Assert.AreEqual(1, result.Items.Count, "The final page should carry the remainder of the seven rows.");
                    Assert.IsNull(result.NextCursor, "The final page must not hand out a cursor.");
                    break;
                }

                Assert.AreEqual(3, result.Items.Count);
                cursor = result.NextCursor;
                Assert.IsNotNull(cursor, "A page with HasMore must carry the cursor for the next one.");
            }

            Assert.AreEqual(7, actual.Count, "Keyset paging must return every seeded row.");
            Assert.AreEqual(actual.Count, actual.Distinct().Count(),
                "Keyset paging must not duplicate an item across page boundaries.");
            CollectionAssert.AreEquivalent(expected.ToArray(), actual.ToArray());
        }
        finally
        {
            writeDb.TaskItems.RemoveRange(seeded);
            await writeDb.SaveChangesAsync(
                OptimisticConcurrencyWinner.ClientWins,
                cancellationToken: CancellationToken.None);
        }
    }

    /// <summary>
    /// Proves the page is projected by the database and costs one round trip: the pager applies the DTO
    /// selector after the resume predicate and the ordering (package 1.1.102 <c>KeysetPageProjectionAsync</c>),
    /// so the SELECT list must carry the DTO's columns and none of the entity-only ones, and the whole page
    /// must be a single command. A regression to client evaluation shows up as extra columns; a regression
    /// to entity paging plus an in-memory Select shows up as entity-only columns in the SELECT list.
    /// </summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task KeysetPage_ProjectsServerSide_InOneRoundTrip()
    {
        var title = $"KeysetProjection-{Guid.NewGuid():N}";
        var seeded = Enumerable.Range(0, 3)
            .Select(_ => new TaskItemBuilder().WithTenantId(QueryTenantId).WithTitle(title).Build())
            .ToArray();

        await using var writeDb = DbContainerFixture.CreateTrxnContext();
        writeDb.TaskItems.AddRange(seeded);
        await writeDb.SaveChangesAsync(
            OptimisticConcurrencyWinner.ClientWins,
            cancellationToken: TestContext.CancellationToken);

        try
        {
            var recorder = new CommandRecordingInterceptor();
            await using var queryDb = DbContainerFixture.CreateQueryContext(null, recorder);
            var repository = new TaskItemRepositoryQuery(queryDb, TestColumnEncryption.Keys, TestCursorCodec.Instance);

            var page = await repository.SearchTaskItemsAsync(
                new TaskItemCursorSearchRequest
                {
                    Filter = new TaskItemSearchFilter { SearchTerm = title, TenantId = QueryTenantId },
                    SortMode = TaskItemSortMode.ModifiedDesc,
                    PageSize = 2
                },
                QueryTenantId,
                TestContext.CancellationToken);

            Assert.AreEqual(2, page.Items.Count);
            Assert.IsTrue(page.HasMore);
            Assert.HasCount(1, recorder.Commands, "One page must be one command, not a page query plus a count or a second read.");

            var sql = recorder.Commands[0];
            var selectList = sql[..sql.IndexOf("FROM", StringComparison.OrdinalIgnoreCase)];

            // Projected: on the DTO. Not projected: written by the entity but absent from TaskItemDto.
            StringAssert.Contains(selectList, "Title");
            StringAssert.Contains(selectList, "EstimatedEffort");
            Assert.IsFalse(selectList.Contains("CreatedAtUtc", StringComparison.Ordinal),
                $"CreatedAtUtc is not on TaskItemDto; selecting it means the entity was materialized. SQL: {sql}");
            Assert.IsFalse(selectList.Contains("RecurrencePattern", StringComparison.Ordinal),
                $"RecurrencePattern is not on TaskItemDto; selecting it means the entity was materialized. SQL: {sql}");

            // The resume predicate and the order must be in the same statement as the projection.
            StringAssert.Contains(sql, "ORDER BY");
        }
        finally
        {
            writeDb.TaskItems.RemoveRange(seeded);
            await writeDb.SaveChangesAsync(
                OptimisticConcurrencyWinner.ClientWins,
                cancellationToken: CancellationToken.None);
        }
    }

    /// <summary>Records the SQL of every reader command executed on the context it is attached to.</summary>
    private sealed class CommandRecordingInterceptor : DbCommandInterceptor
    {
        private readonly List<string> _commands = [];

        public IReadOnlyList<string> Commands
        {
            get { lock (_commands) return [.. _commands]; }
        }

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Record(command);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Record(command);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void Record(DbCommand command)
        {
            lock (_commands) _commands.Add(command.CommandText);
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
