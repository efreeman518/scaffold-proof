using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Model.ValueObjects;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support;

namespace Test.Integration;

/// <summary>
/// D-073: a set-based write to a versioned row (ExecuteUpdate, the insert-if-absent upsert) bypasses the EF.Data save
/// stamp, so it stamps the row itself: an update bumps <c>Version</c> and <c>ModifiedAtUtc</c>, an insert stores
/// <c>Version = 1</c> and the created and modified times, exactly as a tracked save would. Otherwise a client holding
/// the row's old ETag would still pass If-Match and overwrite the set-based change. The category delete's task detach
/// is proven through the API host; the scheduler writes through <see cref="TaskItemSystemRepository"/>. Runs on the
/// lane's relational provider (PostgreSQL on NonAzure, SQL Server on Azure).
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class SetBasedVersionStampTests
{
    private static string _connectionString = null!;
    private static FlowEngineWorkflowApiFactory? _factory;

    public TestContext TestContext { get; set; } = null!;

    /// <summary>Migrates one isolated database and boots one API host on it.</summary>
    [ClassInitialize]
    public static async Task ClassInit(TestContext context)
    {
        if (IntegrationTestSetup.IsUnavailable(DbContainerFixture.StartupError))
            return;
        var ct = context.CancellationToken;
        _connectionString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("TaskFlow_SetBasedStamp", ct);
        await using (var trxn = DbContainerFixture.CreateTrxnContext(_connectionString))
            await trxn.Database.MigrateAsync(ct);
        await using (var flowEngine = DbContainerFixture.CreateFlowEngineContext(_connectionString))
            await flowEngine.Database.MigrateAsync(ct);
        _factory = new FlowEngineWorkflowApiFactory(_connectionString, _ => "{}");
    }

    /// <summary>Disposes the shared API host.</summary>
    [ClassCleanup]
    public static void ClassCleanup() => _factory?.Dispose();

    /// <summary>Inconclusive without a container runtime; fails when the SQL container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("SQL", DbContainerFixture.StartupError);

    /// <summary>
    /// Deleting a category detaches its tasks with an ExecuteUpdate. A client that read the task before the delete and
    /// PUTs it back with that ETag must get 412: the detach moved the task's version. Before, the stale ETag still
    /// matched and the PUT tried to restore the deleted category id.
    /// </summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task Put_WithTheETagReadBeforeTheCategoryDetach_Returns412()
    {
        var ct = TestContext.CancellationToken;
        using var client = _factory!.CreateClient();
        var categoryId = await CreateAsync(client, "/api/v1/categories", new JsonObject { ["name"] = $"Detach-{Guid.NewGuid():N}"[..20] }, ct);
        var taskId = await CreateAsync(client, "/api/v1/task-items", new JsonObject { ["title"] = $"Detach-{Guid.NewGuid():N}", ["categoryId"] = categoryId.ToString() }, ct);

        using var read = await client.GetAsync($"/api/v1/task-items/{taskId}", ct);
        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        var staleETag = read.Headers.ETag!.ToString();
        var staleItem = JsonNode.Parse(await read.Content.ReadAsStringAsync(ct))!["item"]!;

        using (var delete = new HttpRequestMessage(HttpMethod.Delete, $"/api/v1/categories/{categoryId}"))
        {
            delete.Headers.TryAddWithoutValidation("If-Match", "*");
            using var deleted = await client.SendAsync(delete, ct);
            Assert.AreEqual(HttpStatusCode.NoContent, deleted.StatusCode, await deleted.Content.ReadAsStringAsync(ct));
        }

        using var put = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/task-items/{taskId}")
        {
            Content = JsonContent.Create(new JsonObject { ["item"] = staleItem.DeepClone() })
        };
        put.Headers.TryAddWithoutValidation("If-Match", staleETag);
        using var response = await client.SendAsync(put, ct);

        Assert.AreEqual(HttpStatusCode.PreconditionFailed, response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        await using var verify = DbContainerFixture.CreateTrxnContext(_connectionString);
        Assert.IsNull(await verify.TaskItems.IgnoreQueryFilters().Where(t => t.Id == TaskItemId.From(taskId))
            .Select(t => t.CategoryId).SingleAsync(ct), "the detach stands");
    }

    /// <summary>The scheduler's per-row overdue mark bumps the task's Version and ModifiedAtUtc.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task MarkOverdueNotified_BumpsVersionAndModifiedAtUtc()
    {
        var ct = TestContext.CancellationToken;
        var task = TaskItem.Create(TenantId.From(TestConstants.TenantId), $"Overdue-{Guid.NewGuid():N}").Value!;
        task.UpdateDateRange(null, DateTimeOffset.UtcNow.AddDays(-2));
        var before = await SeedAsync(task, ct);

        await using (var db = DbContainerFixture.CreateTrxnContext(_connectionString))
            Assert.IsTrue(await new TaskItemSystemRepository(db).MarkOverdueNotifiedAsync(
                TestConstants.TenantId, task.Id.Value, DateTimeOffset.UtcNow, ct));

        await AssertBumpedAsync(task.Id, before, ct);
    }

    /// <summary>The recurrence template's guarded pointer advance bumps the template's Version and ModifiedAtUtc.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task AdvanceNextOccurrence_BumpsVersionAndModifiedAtUtc()
    {
        var ct = TestContext.CancellationToken;
        var template = TaskItem.Create(TenantId.From(TestConstants.TenantId), $"Template-{Guid.NewGuid():N}").Value!;
        template.Update(features: TaskFeatures.Recurring);
        template.UpdateDateRange(null, DateTimeOffset.UtcNow.AddDays(-3));
        template.UpdateRecurrencePattern(new RecurrencePattern { Frequency = RecurrencePattern.Daily, Interval = 1 });
        var before = await SeedAsync(template, ct);
        var expectedNext = template.NextOccurrenceAtUtc!.Value;

        await using (var db = DbContainerFixture.CreateTrxnContext(_connectionString))
            Assert.IsTrue(await new TaskItemSystemRepository(db).AdvanceNextOccurrenceAsync(
                TestConstants.TenantId, template.Id.Value, expectedNext, expectedNext.AddDays(1), ct));

        await AssertBumpedAsync(template.Id, before, ct);
    }

    /// <summary>The insert-if-absent occurrence upsert stores Version 1 and the created and modified times, as a tracked insert does.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task InsertOccurrenceIfAbsent_StampsTheRowAsATrackedInsertWould()
    {
        var ct = TestContext.CancellationToken;
        var occurrenceUtc = DateTimeOffset.UtcNow.AddDays(-1);
        var occurrence = TaskItem.CreateOccurrence(TenantId.From(TestConstants.TenantId), TaskItemId.From(Guid.CreateVersion7()),
            TaskItemId.From(Guid.CreateVersion7()), occurrenceUtc, $"Occurrence-{Guid.NewGuid():N}").Value!;
        var start = DateTimeOffset.UtcNow.AddSeconds(-5);

        await using (var db = DbContainerFixture.CreateTrxnContext(_connectionString))
            Assert.IsTrue(await new TaskItemSystemRepository(db).InsertOccurrenceIfAbsentAsync(occurrence, ct));

        var stored = await ReadStampAsync(occurrence.Id, ct);
        Assert.AreEqual(1, stored.Version, "a tracked insert stores Version 1");
        Assert.IsGreaterThanOrEqualTo(start, stored.CreatedAtUtc);
        Assert.AreEqual(stored.CreatedAtUtc, stored.ModifiedAtUtc);
    }

    private static async Task<(long Version, DateTimeOffset CreatedAtUtc, DateTimeOffset ModifiedAtUtc)> SeedAsync(TaskItem task, CancellationToken ct)
    {
        await using (var seed = DbContainerFixture.CreateTrxnContext(_connectionString))
        {
            seed.TaskItems.Add(task);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);
        }
        return await ReadStampAsync(task.Id, ct);
    }

    private static async Task AssertBumpedAsync(TaskItemId id, (long Version, DateTimeOffset CreatedAtUtc, DateTimeOffset ModifiedAtUtc) before, CancellationToken ct)
    {
        var after = await ReadStampAsync(id, ct);
        Assert.AreEqual(before.Version + 1, after.Version, "the set-based write bumps Version");
        Assert.IsGreaterThan(before.ModifiedAtUtc, after.ModifiedAtUtc, "and ModifiedAtUtc");
        Assert.AreEqual(before.CreatedAtUtc, after.CreatedAtUtc, "and leaves CreatedAtUtc");
    }

    private static async Task<(long Version, DateTimeOffset CreatedAtUtc, DateTimeOffset ModifiedAtUtc)> ReadStampAsync(TaskItemId id, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(_connectionString);
        var row = await db.TaskItems.IgnoreQueryFilters().AsNoTracking().Where(t => t.Id == id)
            .Select(t => new { t.Version, t.CreatedAtUtc, t.ModifiedAtUtc }).SingleAsync(ct);
        return (row.Version, row.CreatedAtUtc, row.ModifiedAtUtc);
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string url, JsonObject item, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(url, new JsonObject { ["item"] = item }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, body);
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("item").GetProperty("id").GetGuid();
    }
}
