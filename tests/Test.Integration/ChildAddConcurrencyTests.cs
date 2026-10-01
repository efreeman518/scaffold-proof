using EF.Common.Contracts;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Cqrs.Features.TaskItems;
using TaskFlow.Application.Models;
using TaskFlow.Application.Services;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support;
using static Test.Integration.Infrastructure.RaceHarness;

namespace Test.Integration;

/// <summary>
/// D-073: a child add (comment, checklist item, tag association) carries no If-Match, so it must survive a race with
/// another write to the same aggregate. Every child write bumps the root <c>Version</c> (D-031), so the add that saves
/// second loses the optimistic check; the handler re-reads the root through <c>RetryOnConcurrencyAsync</c> and saves
/// again instead of answering 412. The race is staged by an interceptor that commits a competing comment once, just
/// before the handler's first save. Both the service and the CQRS handler are exercised, on the lane's relational
/// provider (PostgreSQL on NonAzure, SQL Server on Azure); the in-memory provider has no concurrency check to lose.
/// Component tier: standalone SQL Testcontainer via <c>DbContainerFixture</c>.
/// </summary>
[TestClass]
public class ChildAddConcurrencyTests
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

    /// <summary>A comment added while another comment lands on the same task is saved, next to the other one.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_ConcurrentChildWrite_When_CommentAdded_Then_RetriesAndKeepsBoth(string style)
    {
        var taskId = await SeedTaskAsync(TestContext.CancellationToken);
        var dto = new CommentDto { Id = Guid.CreateVersion7(), Body = "raced comment" };

        var (response, race) = await RunRacedAsync(taskId, style,
            (service, ct) => service.AddCommentAsync(taskId, dto, ct),
            (repo, ct) => new AddTaskItemCommentHandler(NullLogger<AddTaskItemCommentHandler>.Instance, RequestContext(), repo, Boundary)
                .HandleAsync(new AddTaskItemCommentCommand(taskId, dto), ct));

        Assert.IsFalse(response.IsReplay);
        await using var verify = DbContainerFixture.CreateTrxnContext();
        var bodies = await verify.Comments.IgnoreQueryFilters().Where(c => c.TaskItemId == TaskItemId.From(taskId))
            .Select(c => c.Body).ToListAsync(TestContext.CancellationToken);
        CollectionAssert.AreEquivalent(new[] { CompetingWrite.CompetingBody, dto.Body }, bodies);
        await AssertRetriedAsync(verify, taskId, race, response.AggregateVersion);
    }

    /// <summary>A checklist item added while a comment lands on the same task is saved.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_ConcurrentChildWrite_When_ChecklistItemAdded_Then_RetriesAndSaves(string style)
    {
        var taskId = await SeedTaskAsync(TestContext.CancellationToken);
        var dto = new ChecklistItemDto { Id = Guid.CreateVersion7(), Title = "raced step", SortOrder = 1 };

        var (response, race) = await RunRacedAsync(taskId, style,
            (service, ct) => service.AddChecklistItemAsync(taskId, dto, ct),
            (repo, ct) => new AddTaskItemChecklistItemHandler(NullLogger<AddTaskItemChecklistItemHandler>.Instance, RequestContext(), repo, Boundary)
                .HandleAsync(new AddTaskItemChecklistItemCommand(taskId, dto), ct));

        Assert.IsFalse(response.IsReplay);
        await using var verify = DbContainerFixture.CreateTrxnContext();
        var titles = await verify.ChecklistItems.IgnoreQueryFilters().Where(c => c.TaskItemId == TaskItemId.From(taskId))
            .Select(c => c.Title).ToListAsync(TestContext.CancellationToken);
        CollectionAssert.AreEquivalent(new[] { dto.Title }, titles);
        await AssertRetriedAsync(verify, taskId, race, response.AggregateVersion);
    }

    /// <summary>A tag associated while a comment lands on the same task is saved.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_ConcurrentChildWrite_When_TagAssociated_Then_RetriesAndSaves(string style)
    {
        var taskId = await SeedTaskAsync(TestContext.CancellationToken);
        var tagId = await SeedTagAsync();

        var (response, race) = await RunRacedAsync(taskId, style,
            (service, ct) => service.AssociateTagAsync(taskId, tagId, ct),
            (repo, ct) => new AssociateTaskItemTagHandler(NullLogger<AssociateTaskItemTagHandler>.Instance, RequestContext(), repo, Boundary)
                .HandleAsync(new AssociateTaskItemTagCommand(taskId, tagId), ct));

        Assert.IsFalse(response.IsReplay);
        await using var verify = DbContainerFixture.CreateTrxnContext();
        var tags = await verify.TaskItemTags.IgnoreQueryFilters().Where(t => t.TaskItemId == TaskItemId.From(taskId))
            .Select(t => t.TagId).ToListAsync(TestContext.CancellationToken);
        CollectionAssert.AreEquivalent(new[] { TagId.From(tagId) }, tags);
        await AssertRetriedAsync(verify, taskId, race, response.AggregateVersion);
    }

    /// <summary>
    /// A retried add whose first request stored the same caller id while this one was saving replays it instead of
    /// failing on the duplicate key. The competing write inserts only the child, so this save's root UPDATE passes and
    /// its child INSERT hits the key: the order EF may pick when both requests also bump the root.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_SameIdStoredConcurrently_When_CommentAdded_Then_Replays(string style)
    {
        var taskId = await SeedTaskAsync(TestContext.CancellationToken);
        var dto = new CommentDto { Id = Guid.CreateVersion7(), Body = "same-id comment" };
        var race = new CompetingWrite(DbContainerFixture.ConnectionString, taskId, (other, ct) =>
        {
            other.Comments.Add(Comment.Create(TenantId.From(TenantGuid), TaskItemId.From(taskId), dto.Body, CommentId.From(dto.Id!.Value)).Value!);
            return Task.CompletedTask;
        });

        var (response, _) = await RunRacedAsync(taskId, style,
            (service, ct) => service.AddCommentAsync(taskId, dto, ct),
            (repo, ct) => new AddTaskItemCommentHandler(NullLogger<AddTaskItemCommentHandler>.Instance, RequestContext(), repo, Boundary)
                .HandleAsync(new AddTaskItemCommentCommand(taskId, dto), ct),
            race);

        Assert.IsTrue(response.IsReplay, "the stored row with the caller id is replayed");
        Assert.AreEqual(dto.Id, response.Item!.Id);
        await using var verify = DbContainerFixture.CreateTrxnContext();
        Assert.AreEqual(1, await verify.Comments.IgnoreQueryFilters().CountAsync(c => c.TaskItemId == TaskItemId.From(taskId), TestContext.CancellationToken));
    }

    /// <summary>The same-id race for a checklist item replays the stored item.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_SameIdStoredConcurrently_When_ChecklistItemAdded_Then_Replays(string style)
    {
        var taskId = await SeedTaskAsync(TestContext.CancellationToken);
        var dto = new ChecklistItemDto { Id = Guid.CreateVersion7(), Title = "same-id step", SortOrder = 2 };
        var race = new CompetingWrite(DbContainerFixture.ConnectionString, taskId, (other, ct) =>
        {
            other.ChecklistItems.Add(ChecklistItem.Create(TenantId.From(TenantGuid), TaskItemId.From(taskId), dto.Title, dto.SortOrder, ChecklistItemId.From(dto.Id!.Value)).Value!);
            return Task.CompletedTask;
        });

        var (response, _) = await RunRacedAsync(taskId, style,
            (service, ct) => service.AddChecklistItemAsync(taskId, dto, ct),
            (repo, ct) => new AddTaskItemChecklistItemHandler(NullLogger<AddTaskItemChecklistItemHandler>.Instance, RequestContext(), repo, Boundary)
                .HandleAsync(new AddTaskItemChecklistItemCommand(taskId, dto), ct),
            race);

        Assert.IsTrue(response.IsReplay, "the stored row with the caller id is replayed");
        Assert.AreEqual(dto.Id, response.Item!.Id);
        await using var verify = DbContainerFixture.CreateTrxnContext();
        Assert.AreEqual(1, await verify.ChecklistItems.IgnoreQueryFilters().CountAsync(c => c.TaskItemId == TaskItemId.From(taskId), TestContext.CancellationToken));
    }

    /// <summary>The same race for a tag association (unique on tenant, task and tag) replays the stored association.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_SameTagStoredConcurrently_When_TagAssociated_Then_Replays(string style)
    {
        var taskId = await SeedTaskAsync(TestContext.CancellationToken);
        var tagId = await SeedTagAsync();
        var race = new CompetingWrite(DbContainerFixture.ConnectionString, taskId, (other, ct) =>
        {
            other.TaskItemTags.Add(TaskItemTag.Create(TenantId.From(TenantGuid), TaskItemId.From(taskId), TagId.From(tagId)).Value!);
            return Task.CompletedTask;
        });

        var (response, _) = await RunRacedAsync(taskId, style,
            (service, ct) => service.AssociateTagAsync(taskId, tagId, ct),
            (repo, ct) => new AssociateTaskItemTagHandler(NullLogger<AssociateTaskItemTagHandler>.Instance, RequestContext(), repo, Boundary)
                .HandleAsync(new AssociateTaskItemTagCommand(taskId, tagId), ct),
            race);

        Assert.IsTrue(response.IsReplay, "the stored association is replayed");
        await using var verify = DbContainerFixture.CreateTrxnContext();
        Assert.AreEqual(1, await verify.TaskItemTags.IgnoreQueryFilters().CountAsync(t => t.TaskItemId == TaskItemId.From(taskId), TestContext.CancellationToken));
    }

    /// <summary>
    /// When another write lands before every attempt, the add gives up after the retry budget with 409: the caller sent
    /// no precondition, so a 412 would name one it never made.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_RootChangedBeforeEveryAttempt_When_CommentAdded_Then_Conflict(string style)
    {
        var taskId = await SeedTaskAsync(TestContext.CancellationToken);
        var dto = new CommentDto { Id = Guid.CreateVersion7(), Body = "never saved" };
        var race = new CompetingWrite(DbContainerFixture.ConnectionString, taskId, everySave: true);

        await Assert.ThrowsExactlyAsync<ConflictException>(() => InvokeRacedAsync(style,
            (service, ct) => service.AddCommentAsync(taskId, dto, ct),
            (repo, ct) => new AddTaskItemCommentHandler(NullLogger<AddTaskItemCommentHandler>.Instance, RequestContext(), repo, Boundary)
                .HandleAsync(new AddTaskItemCommentCommand(taskId, dto), ct),
            race));

        Assert.AreEqual(3, race.Saves, "the default retry budget is three attempts");
        await using var verify = DbContainerFixture.CreateTrxnContext();
        Assert.IsFalse(await verify.Comments.IgnoreQueryFilters().AnyAsync(c => c.Id == CommentId.From(dto.Id!.Value), TestContext.CancellationToken));
    }

    /// <summary>Runs one child add through the chosen style and asserts it succeeded.</summary>
    private async Task<(DefaultResponse<T> Response, CompetingWrite Race)> RunRacedAsync<T>(
        Guid taskId,
        string style,
        Func<TaskItemService, CancellationToken, Task<Result<DefaultResponse<T>>>> viaService,
        Func<TaskItemRepositoryTrxn, CancellationToken, Task<Result<DefaultResponse<T>>>> viaCqrs,
        CompetingWrite? race = null)
    {
        race ??= new CompetingWrite(DbContainerFixture.ConnectionString, taskId);
        var result = await InvokeRacedAsync(style, viaService, viaCqrs, race);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.IsNotNull(result.Value!.Item);
        return (result.Value, race);
    }

    /// <summary>Runs one child add through the chosen style on a context the competing write intercepts.</summary>
    private async Task<Result<DefaultResponse<T>>> InvokeRacedAsync<T>(
        string style,
        Func<TaskItemService, CancellationToken, Task<Result<DefaultResponse<T>>>> viaService,
        Func<TaskItemRepositoryTrxn, CancellationToken, Task<Result<DefaultResponse<T>>>> viaCqrs,
        CompetingWrite race)
    {
        var connStr = DbContainerFixture.ConnectionString;
        await using var db = DbContainerFixture.CreateTrxnContext(connStr, race);
        await using var queryDb = DbContainerFixture.CreateQueryContext(connStr);
        var repo = new TaskItemRepositoryTrxn(db);
        var ct = TestContext.CancellationToken;

        return style == Service
            ? await viaService(new TaskItemService(
                NullLogger<TaskItemService>.Instance,
                RequestContext(),
                repo,
                new TaskItemRepositoryQuery(queryDb, TestColumnEncryption.Keys, TestCursorCodec.Instance),
                Boundary,
                Cache), ct)
            : await viaCqrs(repo, ct);
    }

    /// <summary>The competing write landed, the first save lost to it, and the retried save committed on top of it.</summary>
    private async Task AssertRetriedAsync(TaskFlowDbContextTrxn verify, Guid taskId, CompetingWrite race, long? reportedVersion)
    {
        Assert.IsTrue(race.Ran, "the competing write must run before the handler's first save");
        Assert.AreEqual(2, race.Saves, "one lost save and one retried save");

        var root = await verify.TaskItems.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(t => t.Id == TaskItemId.From(taskId), TestContext.CancellationToken);
        Assert.AreEqual(race.SeededVersion + 2, root.Version, "the competing write and the retried add each bump the root once");
        Assert.AreEqual<long?>(root.Version, reportedVersion, "the response reports the version the retried save wrote");
    }

    private async Task<Guid> SeedTagAsync()
    {
        await using var db = DbContainerFixture.CreateTrxnContext();
        var tag = Tag.Create(TenantId.From(TenantGuid), $"Race-{Guid.NewGuid():N}"[..20]).Value!;
        db.Tags.Add(tag);
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: TestContext.CancellationToken);
        return tag.Id.Value;
    }

    public TestContext TestContext { get; set; } = null!;
}
