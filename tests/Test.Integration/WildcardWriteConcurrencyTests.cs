using EF.Common.Contracts;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Application.Cqrs.Features.TaskItems;
using TaskFlow.Application.Mappers;
using TaskFlow.Application.Models;
using TaskFlow.Application.Services;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support;
using static Test.Integration.Infrastructure.RaceHarness;

namespace Test.Integration;

/// <summary>
/// D-032/D-073: <c>If-Match: *</c> states no precondition, so an edit or delete sent with it must survive a race with
/// another write to the same aggregate instead of answering 412. The wildcard path runs its read, apply and one save
/// inside <c>RetryOnConcurrencyAsync</c>; a concrete If-Match keeps the bare <c>Throw</c> save and its 412. The race is
/// staged by <see cref="CompetingWrite"/>, which commits a competing comment through the root just before the
/// handler's first save. Both the service and the CQRS handler are exercised, on the lane's relational provider
/// (PostgreSQL on NonAzure, SQL Server on Azure); the in-memory provider has no concurrency check to lose.
/// Component tier: standalone SQL Testcontainer via <c>DbContainerFixture</c>.
/// </summary>
[TestClass]
public class WildcardWriteConcurrencyTests
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

    /// <summary>A wildcard PATCH that loses the race re-reads and applies its fields on top of the competing write.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_ConcurrentWrite_When_PatchedWithWildcard_Then_RetriesAndApplies(string style)
    {
        var taskId = await SeedTaskAsync(TestContext.CancellationToken);
        var patch = new TaskItemPatchDto { Title = "patched over the race" };
        var race = new CompetingWrite(DbContainerFixture.ConnectionString, taskId);

        var result = await InvokeRacedAsync(style,
            (service, ct) => service.PatchAsync(taskId, patch, expectedVersion: null, ct),
            (repo, ct) => new PatchTaskItemHandler(NullLogger<PatchTaskItemHandler>.Instance, RequestContext(), repo, Boundary, Cache)
                .HandleAsync(new PatchTaskItemCommand(taskId, patch, ExpectedVersion: null), ct),
            race);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        var root = await AssertRetriedAsync(taskId, race);
        Assert.AreEqual(patch.Title, root!.Title);
        Assert.AreEqual<long?>(root.Version, result.Value!.Item!.Version, "the response reports the version the retried save wrote");
        Assert.AreEqual(1, await CommentCountAsync(taskId), "the competing comment survives a PATCH");
    }

    /// <summary>
    /// A wildcard PUT that loses the race re-reads the aggregate and replaces it: the child set it decides on is the
    /// fresh one, so the competing comment the caller did not send is removed with the rest of the replace.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_ConcurrentWrite_When_PutWithWildcard_Then_RetriesAndReplaces(string style)
    {
        var taskId = await SeedTaskAsync(TestContext.CancellationToken);
        var dto = (await ReadRootAsync(taskId))!.ToDto();
        dto.Title = "put over the race";
        var race = new CompetingWrite(DbContainerFixture.ConnectionString, taskId);

        var result = await InvokeRacedAsync(style,
            (service, ct) => service.UpdateAsync(new DefaultRequest<TaskItemDto> { Item = dto }, expectedVersion: null, ct),
            (repo, ct) => new UpdateTaskItemHandler(NullLogger<UpdateTaskItemHandler>.Instance, RequestContext(), repo, Boundary, Cache)
                .HandleAsync(new UpdateTaskItemCommand(new DefaultRequest<TaskItemDto> { Item = dto }, ExpectedVersion: null), ct),
            race);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        var root = await AssertRetriedAsync(taskId, race);
        Assert.AreEqual(dto.Title, root!.Title);
        Assert.AreEqual<long?>(root.Version, result.Value!.Item!.Version, "the response reports the version the retried save wrote");
        Assert.AreEqual(0, await CommentCountAsync(taskId), "the retried PUT replaced the fresh child set, which held the competing comment");
    }

    /// <summary>A wildcard DELETE that loses the race re-reads and deletes the task with the competing comment.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_ConcurrentWrite_When_DeletedWithWildcard_Then_RetriesAndDeletes(string style)
    {
        var taskId = await SeedTaskAsync(TestContext.CancellationToken);
        var race = new CompetingWrite(DbContainerFixture.ConnectionString, taskId);

        var result = await InvokeRacedAsync(style,
            (service, ct) => service.DeleteAsync(taskId, expectedVersion: null, ct),
            (repo, ct) => new DeleteTaskItemHandler(NullLogger<DeleteTaskItemHandler>.Instance, RequestContext(), repo, Boundary, Cache)
                .HandleAsync(new DeleteTaskItemCommand(taskId, ExpectedVersion: null), ct),
            race);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.IsTrue(race.Ran, "the competing write must run before the handler's first save");
        Assert.AreEqual(2, race.Saves, "one lost save and one retried save");
        Assert.IsNull(await ReadRootAsync(taskId), "the retried delete removed the task");
        Assert.AreEqual(0, await CommentCountAsync(taskId), "the competing comment went with the task");
    }

    /// <summary>A wildcard child PUT (comment edit) that loses the race re-reads the root and saves the edit.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_ConcurrentWrite_When_CommentUpdatedWithWildcard_Then_RetriesAndSaves(string style)
    {
        var taskId = await SeedTaskAsync(TestContext.CancellationToken);
        var commentId = await SeedCommentAsync(taskId);
        var dto = new CommentDto { Body = "edited over the race" };
        var race = new CompetingWrite(DbContainerFixture.ConnectionString, taskId);

        var result = await InvokeRacedAsync(style,
            (service, ct) => service.UpdateCommentAsync(taskId, commentId, dto, expectedVersion: null, ct),
            (repo, ct) => new UpdateTaskItemCommentHandler(NullLogger<UpdateTaskItemCommentHandler>.Instance, RequestContext(), repo, Boundary)
                .HandleAsync(new UpdateTaskItemCommentCommand(taskId, commentId, dto, ExpectedVersion: null), ct),
            race);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        var root = await AssertRetriedAsync(taskId, race);
        Assert.AreEqual<long?>(root!.Version, result.Value!.AggregateVersion, "the response reports the root version the retried save wrote");
        await using var verify = DbContainerFixture.CreateTrxnContext();
        var body = await verify.Comments.IgnoreQueryFilters().Where(c => c.Id == CommentId.From(commentId))
            .Select(c => c.Body).SingleAsync(TestContext.CancellationToken);
        Assert.AreEqual(dto.Body, body);
    }

    /// <summary>
    /// When another write lands before every attempt, the wildcard write gives up after the retry budget with 409: the
    /// caller sent no precondition, so a 412 would name one it never made.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_RootChangedBeforeEveryAttempt_When_PatchedWithWildcard_Then_Conflict(string style)
    {
        var taskId = await SeedTaskAsync(TestContext.CancellationToken);
        var seededTitle = (await ReadRootAsync(taskId))!.Title;
        var patch = new TaskItemPatchDto { Title = "never saved" };
        var race = new CompetingWrite(DbContainerFixture.ConnectionString, taskId, everySave: true);

        await Assert.ThrowsExactlyAsync<ConflictException>(() => InvokeRacedAsync(style,
            (service, ct) => service.PatchAsync(taskId, patch, expectedVersion: null, ct),
            (repo, ct) => new PatchTaskItemHandler(NullLogger<PatchTaskItemHandler>.Instance, RequestContext(), repo, Boundary, Cache)
                .HandleAsync(new PatchTaskItemCommand(taskId, patch, ExpectedVersion: null), ct),
            race));

        Assert.AreEqual(3, race.Saves, "the default retry budget is three attempts");
        Assert.AreEqual(seededTitle, (await ReadRootAsync(taskId))!.Title);
    }

    /// <summary>
    /// A concrete If-Match that matched at the read but went stale before the save keeps the bare <c>Throw</c> save: one
    /// save, no retry, and 412 (<see cref="PreconditionFailedException"/>), because the caller decided on that version.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    [Timeout(120000, CooperativeCancellation = true)]
    [DataRow(Service)]
    [DataRow(Cqrs)]
    public async Task Given_ConcurrentWrite_When_PatchedWithConcreteVersion_Then_PreconditionFailedWithoutRetry(string style)
    {
        var taskId = await SeedTaskAsync(TestContext.CancellationToken);
        var seeded = (await ReadRootAsync(taskId))!;
        var patch = new TaskItemPatchDto { Title = "stale caller" };
        var race = new CompetingWrite(DbContainerFixture.ConnectionString, taskId);

        await Assert.ThrowsExactlyAsync<PreconditionFailedException>(() => InvokeRacedAsync(style,
            (service, ct) => service.PatchAsync(taskId, patch, seeded.Version, ct),
            (repo, ct) => new PatchTaskItemHandler(NullLogger<PatchTaskItemHandler>.Instance, RequestContext(), repo, Boundary, Cache)
                .HandleAsync(new PatchTaskItemCommand(taskId, patch, seeded.Version), ct),
            race));

        Assert.AreEqual(1, race.Saves, "a concrete If-Match is never retried");
        Assert.AreEqual(seeded.Title, (await ReadRootAsync(taskId))!.Title);
    }

    /// <summary>Runs one write through the chosen style on a context the competing write intercepts.</summary>
    private async Task<TResult> InvokeRacedAsync<TResult>(
        string style,
        Func<TaskItemService, CancellationToken, Task<TResult>> viaService,
        Func<TaskItemRepositoryTrxn, CancellationToken, Task<TResult>> viaCqrs,
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
    private async Task<TaskItem?> AssertRetriedAsync(Guid taskId, CompetingWrite race)
    {
        Assert.IsTrue(race.Ran, "the competing write must run before the handler's first save");
        Assert.AreEqual(2, race.Saves, "one lost save and one retried save");

        var root = await ReadRootAsync(taskId);
        Assert.IsNotNull(root);
        Assert.AreEqual(race.SeededVersion + 2, root.Version, "the competing write and the retried write each bump the root once");
        return root;
    }

    private async Task<TaskItem?> ReadRootAsync(Guid taskId)
    {
        await using var verify = DbContainerFixture.CreateTrxnContext();
        return await verify.TaskItems.IgnoreQueryFilters().AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == TaskItemId.From(taskId), TestContext.CancellationToken);
    }

    private async Task<int> CommentCountAsync(Guid taskId)
    {
        await using var verify = DbContainerFixture.CreateTrxnContext();
        return await verify.Comments.IgnoreQueryFilters()
            .CountAsync(c => c.TaskItemId == TaskItemId.From(taskId), TestContext.CancellationToken);
    }

    private async Task<Guid> SeedCommentAsync(Guid taskId)
    {
        await using var db = DbContainerFixture.CreateTrxnContext();
        var comment = Comment.Create(TenantId.From(TenantGuid), TaskItemId.From(taskId), "seeded comment", CommentId.From(Guid.CreateVersion7())).Value!;
        db.Comments.Add(comment);
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: TestContext.CancellationToken);
        return comment.Id.Value;
    }

    public TestContext TestContext { get; set; } = null!;
}
