using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support;
using Test.Support.Fixtures;

namespace Test.Integration;

/// <summary>
/// Exercises the summary and export paths against the bulk fixture at a reduced row count. Two things are
/// under test that a handful of rows cannot show: the export resumes across batches with no duplicate and no
/// gap, and the summary still costs one round trip when the tenant is large. The same fixture drives the
/// million-row seed script, so a defect in the generator surfaces here rather than during a load run.
/// Component tier: real database on the selected provider lane.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class ScaleFixtureExportTests
{
    /// <summary>
    /// Rows seeded for this lane. 50,000 is enough to page an export many times over while staying inside a
    /// test run; <c>TASKFLOW_SCALE_ROWS</c> overrides it for a heavier local check.
    /// </summary>
    private static int RowCount =>
        int.TryParse(Environment.GetEnvironmentVariable("TASKFLOW_SCALE_ROWS"), out var rows) && rows > 0
            ? rows
            : 50_000;

    private const int ExportBatchSize = 500;

    private static readonly Guid PrimaryTenant = Guid.NewGuid();
    private static readonly Guid SecondaryTenant = Guid.NewGuid();
    private static string _connectionString = string.Empty;

    /// <summary>Seeds an isolated database once for the class: the row count makes a per-test seed untenable.</summary>
    [ClassInitialize]
    public static async Task ClassInit(TestContext context)
    {
        if (IntegrationTestSetup.IsUnavailable(DbContainerFixture.StartupError)) return;

        _connectionString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("scale");
        await using var db = DbContainerFixture.CreateTrxnContext(_connectionString);
        await db.Database.MigrateAsync(context.CancellationToken);

        var fixture = new MillionRowTaskFixture([PrimaryTenant, SecondaryTenant], DateTimeOffset.UtcNow);
        await fixture.SeedAsync(db, RowCount, ct: context.CancellationToken);
    }

    /// <summary>Marks the test Inconclusive when the database container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("database", DbContainerFixture.StartupError);

    /// <summary>Paging the export to the end yields every row of the tenant exactly once.</summary>
    [TestMethod]
    [Timeout(900000, CooperativeCancellation = true)]
    public async Task StreamExportAsync_PagesTheWholeTenant_WithNoDuplicateOrGap()
    {
        await using var query = DbContainerFixture.CreateQueryContext(_connectionString);
        var repository = new TaskItemRepositoryQuery(query, TestColumnEncryption.Keys);

        var expected = await query.Set<TaskItem>()
            .IgnoreQueryFilters()
            .CountAsync(t => t.TenantId == DomainId.From<TenantId>(PrimaryTenant), TestContext.CancellationToken);
        Assert.IsGreaterThan(0, expected, "the skewed fixture must put rows in the primary tenant");

        var seen = new HashSet<Guid>();
        Guid? cursor = null;
        while (true)
        {
            var batch = 0;
            await foreach (var row in repository.StreamExportAsync(
                PrimaryTenant, cursor, ExportBatchSize, TestContext.CancellationToken))
            {
                Assert.IsTrue(seen.Add(row.Id), $"row {row.Id} was exported twice");
                cursor = row.Id;
                batch++;
            }

            if (batch < ExportBatchSize) break;
        }

        Assert.AreEqual(expected, seen.Count, "every row of the tenant was exported exactly once");
    }

    /// <summary>The summary stays a single aggregate query at scale, and the fixture's mix is present.</summary>
    [TestMethod]
    [Timeout(900000, CooperativeCancellation = true)]
    public async Task GetSummaryAsync_AtScale_ReportsTheSeededMix()
    {
        await using var query = DbContainerFixture.CreateQueryContext(_connectionString);
        var repository = new TaskItemRepositoryQuery(query, TestColumnEncryption.Keys);

        var summary = await repository.GetSummaryAsync(PrimaryTenant, TestContext.CancellationToken);

        Assert.IsGreaterThan(0, summary.Total);
        Assert.IsGreaterThan(0, summary.Overdue, "the fixture seeds roughly 8% overdue tasks");
        Assert.AreEqual(summary.Total, summary.ByStatus.Sum(s => s.Count),
            "per-status counts must add up to the total");
    }

    public TestContext TestContext { get; set; } = null!;
}
