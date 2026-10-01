using EF.Cache;
using EF.Common.Contracts;
using EF.Data.Contracts;
using EF.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Cqrs.Features.TaskItems;
using TaskFlow.Application.Models;
using TaskFlow.Application.Services;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Infrastructure.Caching;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support;
using Test.Support.Builders;

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
    private static readonly Guid TenantGuid = TestConstants.TenantId;

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
        var taskId = await SeedTaskAsync();
        var dto = new CommentDto { Id = Guid.CreateVersion7(), Body = "raced comment" };

        var (response, race) = await RunRacedAsync(taskId, style,
            (service, ct) => service.AddCommentAsync(taskId, dto, ct),
            (repo, ct) => new AddTaskItemCommentHandler(NullLogger<AddTaskItemCommentHandler>.Instance, RequestContext(), repo, Boundary)
                .HandleAsync(new AddTaskItemCommentCommand(taskId, dto), ct));

        Assert.IsFalse(response.IsReplay);
        await using var verify = DbContainerFixture.CreateTrxnContext();
        var bodies = await verify.Comments.IgnoreQueryFilters().Where(c => c.TaskItemId == TaskItemId.From(taskId))
            .Select(c => c.Body).ToListAsync(TestContext.CancellationToken);
        CollectionAssert.AreEquivalent(new[] { RaceOnFirstSave.CompetingBody, dto.Body }, bodies);
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
        var taskId = await SeedTaskAsync();
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
        var taskId = await SeedTaskAsync();
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
    /// Runs one child add through the chosen style on a context whose first save is preceded by the competing write.
    /// </summary>
    private async Task<(DefaultResponse<T> Response, RaceOnFirstSave Race)> RunRacedAsync<T>(
        Guid taskId,
        string style,
        Func<TaskItemService, CancellationToken, Task<Result<DefaultResponse<T>>>> viaService,
        Func<TaskItemRepositoryTrxn, CancellationToken, Task<Result<DefaultResponse<T>>>> viaCqrs)
    {
        var connStr = DbContainerFixture.ConnectionString;
        var race = new RaceOnFirstSave(connStr, taskId);
        await using var db = DbContainerFixture.CreateTrxnContext(connStr, race);
        await using var queryDb = DbContainerFixture.CreateQueryContext(connStr);
        var repo = new TaskItemRepositoryTrxn(db);
        var ct = TestContext.CancellationToken;

        var result = style == Service
            ? await viaService(new TaskItemService(
                NullLogger<TaskItemService>.Instance,
                RequestContext(),
                repo,
                new TaskItemRepositoryQuery(queryDb, TestColumnEncryption.Keys, TestCursorCodec.Instance),
                Boundary,
                Cache), ct)
            : await viaCqrs(repo, ct);

        Assert.IsTrue(result.IsSuccess, result.ErrorMessage);
        Assert.IsNotNull(result.Value!.Item);
        return (result.Value, race);
    }

    /// <summary>The competing write landed, the first save lost to it, and the retried save committed on top of it.</summary>
    private async Task AssertRetriedAsync(TaskFlowDbContextTrxn verify, Guid taskId, RaceOnFirstSave race, long? reportedVersion)
    {
        Assert.IsTrue(race.Ran, "the competing write must run before the handler's first save");
        Assert.AreEqual(2, race.Saves, "one lost save and one retried save");

        var root = await verify.TaskItems.IgnoreQueryFilters().AsNoTracking()
            .SingleAsync(t => t.Id == TaskItemId.From(taskId), TestContext.CancellationToken);
        Assert.AreEqual(race.SeededVersion + 2, root.Version, "the competing write and the retried add each bump the root once");
        Assert.AreEqual<long?>(root.Version, reportedVersion, "the response reports the version the retried save wrote");
    }

    private async Task<Guid> SeedTaskAsync()
    {
        await using var db = DbContainerFixture.CreateTrxnContext();
        var task = new TaskItemBuilder().WithTenantId(TenantGuid).WithTitle($"Race-{Guid.NewGuid():N}").Build();
        db.TaskItems.Add(task);
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: TestContext.CancellationToken);
        return task.Id.Value;
    }

    private async Task<Guid> SeedTagAsync()
    {
        await using var db = DbContainerFixture.CreateTrxnContext();
        var tag = Tag.Create(TenantId.From(TenantGuid), $"Race-{Guid.NewGuid():N}"[..20]).Value!;
        db.Tags.Add(tag);
        await db.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: TestContext.CancellationToken);
        return tag.Id.Value;
    }

    private static RequestContext<string, Guid?> RequestContext() =>
        new("race-test", "race-user", TenantGuid, [AppConstants.ROLE_TENANT_MEMBER]);

    private static readonly ServiceProvider Services = BuildServices();
    private static ITenantBoundaryValidator Boundary => Services.GetRequiredService<ITenantBoundaryValidator>();
    private static ITypedCache Cache => Services.GetRequiredService<ITypedCache>();

    /// <summary>The real tenant boundary and an L1-only cache (child adds never touch the cache).</summary>
    private static ServiceProvider BuildServices()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:0:Name"] = AppConstants.DEFAULT_CACHE,
                ["OpenTelemetry:MetricsEnabled"] = "false"
            })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new RaceTestHostEnvironment());
        services.AddTaskFlowCaching(config);
        services.AddTenancy(options => options.CrossTenantRoles = [AppConstants.ROLE_GLOBAL_ADMIN, AppConstants.ROLE_SYSTEM]);
        return services.BuildServiceProvider();
    }

    /// <summary>
    /// Before the first save on the handler's context, commits a comment on the same task from another context, so the
    /// root row's version moves past the one the handler loaded. Runs the competing write once and counts saves.
    /// </summary>
    private sealed class RaceOnFirstSave(string connectionString, Guid taskId) : SaveChangesInterceptor
    {
        public const string CompetingBody = "competing comment";

        public bool Ran { get; private set; }
        public int Saves { get; private set; }
        public long SeededVersion { get; private set; }

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Saves++;
            if (!Ran)
            {
                Ran = true;
                await using var other = DbContainerFixture.CreateTrxnContext(connectionString);
                var root = await new TaskItemRepositoryTrxn(other).GetTaskItemAsync(TaskItemId.From(taskId), inclChildren: false, cancellationToken)
                    ?? throw new InvalidOperationException("seeded task not found");
                SeededVersion = root.Version;
                Assert.IsTrue(root.AddComment(CompetingBody).IsSuccess);
                await other.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: cancellationToken);
            }

            return result;
        }
    }

    private sealed class RaceTestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "IntegrationTest";
        public string ApplicationName { get; set; } = nameof(ChildAddConcurrencyTests);
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    public TestContext TestContext { get; set; } = null!;
}
