using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Application.Cqrs.Features.Categories;
using TaskFlow.Application.Services;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support.Builders;
using static Test.Integration.Infrastructure.RaceHarness;

namespace Test.Integration;

/// <summary>
/// D-022/D-073: deleting a category first detaches the tenant's tasks (a set-based <c>ExecuteUpdate</c>, because the
/// composite FK cannot cascade to SetNull), then deletes the row. Both are one unit of work: when the delete's save
/// fails after the detach, the detach rolls back with it and the tasks keep their category. An interceptor fails the
/// delete's save, and a competing category edit makes it lose a race. Both the service and the CQRS handler are
/// exercised, on the lane's relational provider (PostgreSQL on NonAzure, SQL Server on Azure); the in-memory provider
/// has no set-based update to roll back.
/// Component tier: standalone SQL Testcontainer via <c>DbContainerFixture</c>.
/// </summary>
[TestClass]
public class CategoryDeleteAtomicityTests
{
    private const string Service = "Service";
    private const string Cqrs = "Cqrs";

    /// <summary>Ensures the shared SQL schema exists before this class runs (idempotent migrate).</summary>
    [ClassInitialize]
    public static async Task ClassInit(TestContext context)
    {
        if (IntegrationTestSetup.IsUnavailable(DbContainerFixture.StartupError))
            return;
        await using var db = DbContainerFixture.CreateTrxnContext();
        await db.Database.MigrateAsync(context.CancellationToken);
    }

    /// <summary>Inconclusive without a container runtime; fails when the SQL container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("SQL", DbContainerFixture.StartupError);

    /// <summary>A category delete whose save fails leaves the category and its tasks' references in place.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_DeleteSaveFails_When_CategoryDeleted_Then_TasksKeepTheirCategory(string style)
    {
        var ct = TestContext.CancellationToken;
        var (categoryId, taskId) = await SeedCategoryWithTaskAsync();
        var failSave = new FailingSave();

        var connStr = DbContainerFixture.ConnectionString;
        await using (var db = DbContainerFixture.CreateTrxnContext(connStr, failSave))
        await using (var queryDb = DbContainerFixture.CreateQueryContext(connStr))
        {
            var repo = new CategoryRepositoryTrxn(db);
            var result = style == Service
                ? await new CategoryService(NullLogger<CategoryService>.Instance, RequestContext(), repo,
                    new CategoryRepositoryQuery(queryDb), Boundary, Cache).DeleteAsync(categoryId, expectedVersion: null, ct)
                : await new DeleteCategoryHandler(NullLogger<DeleteCategoryHandler>.Instance, RequestContext(), repo, Boundary, Cache)
                    .HandleAsync(new DeleteCategoryCommand(categoryId, ExpectedVersion: null), ct);

            Assert.IsTrue(result.IsFailure, "the failed save is reported as a failed delete");
        }

        Assert.AreEqual(1, failSave.Saves, "the delete reached its save once and it failed");
        await using var verify = DbContainerFixture.CreateTrxnContext();
        Assert.IsTrue(await verify.Categories.IgnoreQueryFilters().AnyAsync(c => c.Id == CategoryId.From(categoryId), ct),
            "the category was not deleted");
        var stored = await verify.TaskItems.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == TaskItemId.From(taskId)).Select(t => t.CategoryId).SingleAsync(ct);
        Assert.AreEqual(CategoryId.From(categoryId), stored, "the detach rolled back with the failed delete");
    }

    /// <summary>
    /// A wildcard delete whose save loses to a competing category edit rolls its detach back with the lost save, then
    /// re-reads and deletes in a second unit: the retry contract holds with the detach inside the attempt.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_ConcurrentCategoryEdit_When_DeletedWithWildcard_Then_RetriesAndDeletes(string style)
    {
        var ct = TestContext.CancellationToken;
        var (categoryId, taskId) = await SeedCategoryWithTaskAsync();
        var race = new CompetingWrite(DbContainerFixture.ConnectionString, Guid.Empty, async (other, token) =>
        {
            var category = await other.Categories.IgnoreQueryFilters().SingleAsync(c => c.Id == CategoryId.From(categoryId), token);
            Assert.IsTrue(category.Update(description: "competing edit").IsSuccess);
        });

        var connStr = DbContainerFixture.ConnectionString;
        await using (var db = DbContainerFixture.CreateTrxnContext(connStr, race))
        await using (var queryDb = DbContainerFixture.CreateQueryContext(connStr))
        {
            var repo = new CategoryRepositoryTrxn(db);
            var result = style == Service
                ? await new CategoryService(NullLogger<CategoryService>.Instance, RequestContext(), repo,
                    new CategoryRepositoryQuery(queryDb), Boundary, Cache).DeleteAsync(categoryId, expectedVersion: null, ct)
                : await new DeleteCategoryHandler(NullLogger<DeleteCategoryHandler>.Instance, RequestContext(), repo, Boundary, Cache)
                    .HandleAsync(new DeleteCategoryCommand(categoryId, ExpectedVersion: null), ct);

            Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        }

        Assert.AreEqual(2, race.Saves, "one lost save and one retried save");
        await using var verify = DbContainerFixture.CreateTrxnContext();
        Assert.IsFalse(await verify.Categories.IgnoreQueryFilters().AnyAsync(c => c.Id == CategoryId.From(categoryId), ct));
        var stored = await verify.TaskItems.IgnoreQueryFilters().AsNoTracking()
            .Where(t => t.Id == TaskItemId.From(taskId)).Select(t => t.CategoryId).SingleAsync(ct);
        Assert.IsNull(stored, "the retried unit detached the task");
    }

    private async Task<(Guid CategoryId, Guid TaskId)> SeedCategoryWithTaskAsync()
    {
        await using var db = DbContainerFixture.CreateTrxnContext();
        var category = new CategoryBuilder().WithTenantId(TenantGuid).WithName($"Atomic-{Guid.NewGuid():N}"[..20]).Build();
        db.Categories.Add(category);
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: TestContext.CancellationToken);

        var task = new TaskItemBuilder().WithTenantId(TenantGuid).WithTitle($"Atomic-{Guid.NewGuid():N}")
            .WithCategoryId(category.Id.Value).Build();
        db.TaskItems.Add(task);
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: TestContext.CancellationToken);
        return (category.Id.Value, task.Id.Value);
    }

    /// <summary>Fails every save on the context it is attached to, after any set-based statement already ran.</summary>
    private sealed class FailingSave : SaveChangesInterceptor
    {
        public int Saves { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Saves++;
            throw new InvalidOperationException("staged save failure");
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
