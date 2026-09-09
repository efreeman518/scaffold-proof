using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// Validates the typed generic repository pair (<c>RepositoryTrxn&lt;TEntity, TId, TDbContext&gt;</c> /
/// <c>RepositoryQuery&lt;TEntity, TId, TDbContext&gt;</c>, surfaced as
/// <c>TaskFlowRepositoryTrxn&lt;TEntity, TId&gt;</c> / <c>TaskFlowRepositoryQuery&lt;TEntity, TId&gt;</c>) against real SQL:
/// generic <c>Create</c> + tracked <c>GetAsync</c> on the write context, and no-tracking <c>GetAsync</c>
/// + <c>ListAsync</c> on the query context. <c>Tag</c> is used because it is a generic-coverable entity
/// (its write side was folded into the generic pair) with no FK prerequisites.
/// Component tier: standalone SQL Testcontainer via <c>DbContainerFixture</c> - no Aspire graph.
/// </summary>
[TestClass]
public class GenericRepositoryIntegrationTests
{
    // Matches the tenant the query context filters by (see DomainEventPipelineTests), so query-side
    // reads are not excluded by the ITenantEntity query filter.
    private static readonly TenantId TenantId = DomainId.From<TenantId>(Guid.Parse("11111111-1111-1111-1111-111111111111"));

    /// <summary>Ensures the shared SQL schema exists before this class runs (idempotent migrate).</summary>
    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        if (IntegrationTestSetup.IsUnavailable(DbContainerFixture.StartupError))
            return;
        await using var db = DbContainerFixture.CreateTrxnContext();
        await db.Database.MigrateAsync(_.CancellationToken);
    }

    /// <summary>Marks the test Inconclusive when the SQL container failed to start (assembly-init safety).</summary>
    [TestInitialize]
    public void TestSetup()
    {
        IntegrationTestSetup.AssertAvailable("SQL", DbContainerFixture.StartupError);
    }

    /// <summary>Verifies that a tag persisted via the generic Trxn repo is readable by tracked and no-tracking GetAsync.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task Given_TagCreatedViaGenericTrxn_When_GetAsync_Then_ReturnsEntity()
    {
        var connStr = DbContainerFixture.ConnectionString;

        // Arrange + Act (write) - generic RepositoryTrxn.Create + SaveChangesAsync
        await using var trxnCtx = DbContainerFixture.CreateTrxnContext(connStr);
        var trxnRepo = new TaskFlowRepositoryTrxn<Tag, TagId>(trxnCtx);
        var tag = Tag.Create(TenantId, $"GenRepo-{Guid.NewGuid():N}").Value!;
        trxnRepo.Create(ref tag);
        await trxnRepo.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, CancellationToken.None);

        // Assert - tracked GetAsync on the write repo
        var tracked = await trxnRepo.GetAsync(tag.Id, TestContext.CancellationToken);
        Assert.IsNotNull(tracked, "generic Trxn GetAsync should return the persisted tag");
        Assert.AreEqual(tag.Name, tracked.Name);

        // Assert - no-tracking GetAsync on a fresh query context
        await using var queryCtx = DbContainerFixture.CreateQueryContext(connStr);
        var queryRepo = new TaskFlowRepositoryQuery<Tag, TagId>(queryCtx);
        var read = await queryRepo.GetAsync(tag.Id, TestContext.CancellationToken);
        Assert.IsNotNull(read, "generic Query GetAsync should return the persisted tag");
        Assert.AreEqual(tag.Name, read.Name);

        // Assert - GetAsync for a missing id returns null
        Assert.IsNull(await queryRepo.GetAsync(DomainId.From<TagId>(Guid.NewGuid()), TestContext.CancellationToken));
    }

    /// <summary>Verifies the generic Query repo's ListAsync returns exactly the entities matching the predicate.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task Given_MultipleTags_When_ListAsync_Then_ReturnsPredicateMatches()
    {
        var connStr = DbContainerFixture.ConnectionString;
        var prefix = $"GenRepoList-{Guid.NewGuid():N}";

        await using var trxnCtx = DbContainerFixture.CreateTrxnContext(connStr);
        var trxnRepo = new TaskFlowRepositoryTrxn<Tag, TagId>(trxnCtx);
        var tagA = Tag.Create(TenantId, $"{prefix}-A").Value!;
        var tagB = Tag.Create(TenantId, $"{prefix}-B").Value!;
        trxnRepo.Create(ref tagA);
        trxnRepo.Create(ref tagB);
        await trxnRepo.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, CancellationToken.None);

        await using var queryCtx = DbContainerFixture.CreateQueryContext(connStr);
        var queryRepo = new TaskFlowRepositoryQuery<Tag, TagId>(queryCtx);

        var matches = await queryRepo.ListAsync(t => t.Name.StartsWith(prefix), TestContext.CancellationToken);

        Assert.HasCount(2, matches, "ListAsync should return exactly the two tags matching the unique prefix");
        CollectionAssert.AreEquivalent(
            new[] { tagA.Id, tagB.Id },
            matches.Select(t => t.Id).ToList());
    }

    public TestContext TestContext { get; set; } = null!;
}
