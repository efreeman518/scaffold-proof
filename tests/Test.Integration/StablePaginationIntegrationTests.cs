using EF.Common.Contracts;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Model;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support.Builders;

using Test.Support;

namespace Test.Integration;

/// <summary>Proves paged repository searches use a unique final sort against real SQL Server.</summary>
[TestClass]
[TestCategory("Integration")]
public sealed class StablePaginationIntegrationTests
{
    private static readonly Guid QueryTenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    /// <summary>Ensures the shared SQL schema exists before pagination tests run.</summary>
    [ClassInitialize]
    public static async Task ClassInit(TestContext context)
    {
        if (IntegrationTestSetup.IsUnavailable(SqlContainerFixture.StartupError))
            return;

        await using var db = SqlContainerFixture.CreateTrxnContext();
        await db.Database.MigrateAsync(context.CancellationToken);
    }

    /// <summary>Marks the test inconclusive when the SQL container is unavailable.</summary>
    [TestInitialize]
    public void TestSetup() =>
        IntegrationTestSetup.AssertAvailable("SQL", SqlContainerFixture.StartupError);

    /// <summary>Verifies duplicate default sort keys remain stable across every page.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task DefaultSort_WithDuplicateTitles_ReturnsStableCompletePages()
    {
        await AssertStablePagesAsync(sorts: null);
    }

    /// <summary>Verifies caller-supplied sorts also receive the unique ID tie-breaker.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task ExplicitSort_WithDuplicateTitles_ReturnsStableCompletePages()
    {
        await AssertStablePagesAsync([new Sort(nameof(TaskItem.Title), SortOrder.Descending)]);
    }

    private async Task AssertStablePagesAsync(IEnumerable<Sort>? sorts)
    {
        var title = $"StablePaging-{Guid.NewGuid():N}";
        var seeded = Enumerable.Range(0, 7)
            .Select(_ => new TaskItemBuilder()
                .WithTenantId(QueryTenantId)
                .WithTitle(title)
                .Build())
            .ToArray();

        await using var writeDb = SqlContainerFixture.CreateTrxnContext();
        writeDb.TaskItems.AddRange(seeded);
        await writeDb.SaveChangesAsync(
            OptimisticConcurrencyWinner.ClientWins,
            cancellationToken: TestContext.CancellationToken);

        try
        {
            var expected = await writeDb.TaskItems
                .IgnoreQueryFilters()
                .Where(task => task.Title == title)
                .OrderBy(task => task.Title)
                .ThenBy(task => task.Id)
                .Select(task => task.Id.Value)
                .ToArrayAsync(TestContext.CancellationToken);

            await using var queryDb = SqlContainerFixture.CreateQueryContext();
            var repository = new TaskItemRepositoryQuery(queryDb, TestColumnEncryption.Keys);
            var actual = new List<Guid>();

            for (var pageIndex = 1; pageIndex <= 3; pageIndex++)
            {
                var page = await repository.SearchTaskItemsAsync(
                    new SearchRequest<TaskItemSearchFilter>
                    {
                        Filter = new TaskItemSearchFilter
                        {
                            SearchTerm = title,
                            TenantId = QueryTenantId
                        },
                        PageIndex = pageIndex,
                        PageSize = 3,
                        Sorts = sorts
                    },
                    TestContext.CancellationToken);

                Assert.AreEqual(7, page.Total);
                Assert.AreEqual(pageIndex < 3 ? 3 : 1, page.Data.Count);
                actual.AddRange(page.Data.Select(item => item.Id!.Value));
            }

            CollectionAssert.AreEqual(expected, actual);
            Assert.AreEqual(actual.Count, actual.Distinct().Count(),
                "Stable paging must not duplicate an item across page boundaries.");
        }
        finally
        {
            writeDb.TaskItems.RemoveRange(seeded);
            await writeDb.SaveChangesAsync(
                OptimisticConcurrencyWinner.ClientWins,
                cancellationToken: CancellationToken.None);
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
