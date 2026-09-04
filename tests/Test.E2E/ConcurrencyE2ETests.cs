using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Enums;
using Test.Support;

namespace Test.E2E;

/// <summary>
/// Concurrency and streaming behavior over the full HTTP -> Endpoint -> Service -> EF -> SQL stack.
/// These belong on real SQL rather than InMemory: the losing side of a genuine write race is decided by
/// the database's own <c>WHERE Version = @original</c> row count, and the export stream's batching is a
/// property of real query execution.
/// </summary>
[TestClass]
[TestCategory("E2E")]
public class ConcurrencyE2ETests
{
    private static DbApiFactory _factory = null!;
    private static readonly JsonSerializerOptions _json = JsonTestOptions.Default;

    /// <summary>Initializes shared test fixtures before the class-level test run begins.</summary>
    [ClassInitialize]
    public static async Task ClassInit(TestContext context)
    {
        await DbApiFactory.StartContainerAsync(context.CancellationToken);
        if (DbApiFactory.DockerUnavailableReason is not null || DbApiFactory.StartupError is not null)
            return;

        _factory = new DbApiFactory();

        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaskFlow.Infrastructure.Data.TaskFlowDbContextTrxn>();
        await db.Database.MigrateAsync(context.CancellationToken);
    }

    /// <summary>Disposes shared test fixtures after the class-level test run finishes.</summary>
    [ClassCleanup]
    public static void ClassCleanup() => _factory?.Dispose();

    /// <summary>Creates client used by the surrounding test cases.</summary>
    private static HttpClient CreateClient() => _factory.CreateClient();

    /// <summary>Creates a task item and returns the created DTO.</summary>
    private async Task<TaskItemDto> CreateTaskAsync(HttpClient client, string title)
    {
        var response = await client.PostAsJsonAsync("/api/v1/task-items",
            new DefaultRequest<TaskItemDto> { Item = new TaskItemDto { Title = title, Priority = Priority.Medium } },
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode,
            $"Create failed: {await response.Content.ReadAsStringAsync(TestContext.CancellationToken)}");
        return (await response.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_json, TestContext.CancellationToken))!.Item!;
    }

    /// <summary>
    /// Two clients hold the same ETag and write concurrently: exactly one wins and the other gets 412.
    /// The requests are issued together so the losing side is decided by the database row count, which
    /// is the case a sequential stale-write test cannot reach.
    /// </summary>
    [TestMethod]
    public async Task TaskItem_ConcurrentUpdatesWithSameETag_OnlyOneWins_AgainstRealSql()
    {
        using var clientA = CreateClient();
        using var clientB = CreateClient();

        var created = await CreateTaskAsync(clientA, $"Race {Guid.NewGuid():N}");
        var ifMatch = ConcurrencyHttp.IfMatch(created.Version!.Value);

        var updateA = new DefaultRequest<TaskItemDto> { Item = created with { Title = "Winner A" } };
        var updateB = new DefaultRequest<TaskItemDto> { Item = created with { Title = "Winner B" } };

        var taskA = clientA.PutWithIfMatchAsync($"/api/v1/task-items/{created.Id}", updateA, ifMatch, TestContext.CancellationToken);
        var taskB = clientB.PutWithIfMatchAsync($"/api/v1/task-items/{created.Id}", updateB, ifMatch, TestContext.CancellationToken);

        var responses = await Task.WhenAll(taskA, taskB);

        var succeeded = responses.Count(r => r.StatusCode == HttpStatusCode.OK);
        var rejected = responses.Count(r => r.StatusCode == HttpStatusCode.PreconditionFailed);

        Assert.AreEqual(1, succeeded, "Exactly one writer may win a race on the same version.");
        Assert.AreEqual(1, rejected, "The losing writer must be told its copy was stale, not silently dropped.");

        foreach (var response in responses) response.Dispose();

        // The stored row reflects the winner, not a merge of both.
        using var get = await clientA.GetAsync($"/api/v1/task-items/{created.Id}", TestContext.CancellationToken);
        var stored = (await get.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_json, TestContext.CancellationToken))!.Item!;
        Assert.IsTrue(stored.Title is "Winner A" or "Winner B");
        Assert.IsGreaterThan(created.Version!.Value, stored.Version!.Value);
    }

    /// <summary>
    /// Verifies a child mutation moves the root aggregate version against real SQL (D-031): the root
    /// ETag is the aggregate's concurrency currency, so a comment added by one client must invalidate
    /// another client's cached root version.
    /// </summary>
    [TestMethod]
    public async Task TaskItem_ChildMutation_BumpsRootETag_AgainstRealSql()
    {
        using var client = CreateClient();
        var created = await CreateTaskAsync(client, $"Child bump {Guid.NewGuid():N}");

        using var addComment = await client.PostAsJsonAsync(
            $"/api/v1/task-items/{created.Id}/comments",
            new DefaultRequest<CommentDto> { Item = new CommentDto { Body = "bumps the root", TaskItemId = created.Id!.Value } },
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, addComment.StatusCode,
            $"Add comment failed: {await addComment.Content.ReadAsStringAsync(TestContext.CancellationToken)}");

        using var get = await client.GetAsync($"/api/v1/task-items/{created.Id}", TestContext.CancellationToken);
        var reloaded = (await get.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_json, TestContext.CancellationToken))!.Item!;

        Assert.IsGreaterThan(created.Version!.Value, reloaded.Version!.Value,
            "A child write must move the root version, or the root ETag misrepresents the aggregate.");
        Assert.AreEqual(reloaded.Version!.Value.ToString(), addComment.ETagValue(),
            "The child add response must return the new root version.");

        // The pre-child root version is now stale for a root write.
        using var stale = await client.PutWithIfMatchAsync(
            $"/api/v1/task-items/{created.Id}",
            new DefaultRequest<TaskItemDto> { Item = created with { Title = "Stale root write" } },
            ConcurrencyHttp.IfMatch(created.Version!.Value),
            TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.PreconditionFailed, stale.StatusCode);
        Assert.AreEqual(reloaded.Version!.Value.ToString(), stale.ETagValue());
    }

    /// <summary>
    /// Verifies the NDJSON export streams every row across more than one batch against real SQL, which
    /// is where the resume-after-id predicate either works or silently repeats a batch forever.
    /// </summary>
    [TestMethod]
    public async Task TaskItem_Export_StreamsAllRowsAcrossBatches_AgainstRealSql()
    {
        using var client = CreateClient();

        var marker = $"Export {Guid.NewGuid():N}";
        var titles = new List<string>();
        for (var i = 0; i < 5; i++) titles.Add((await CreateTaskAsync(client, $"{marker} {i:D2}")).Title);

        using var response = await client.GetAsync("/api/v1/task-items/export?batchSize=2", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
            $"Export failed: {await response.Content.ReadAsStringAsync(TestContext.CancellationToken)}");
        Assert.AreEqual("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        var exported = body
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("title").GetString())
            .ToList();

        foreach (var title in titles) CollectionAssert.Contains(exported, title);
        Assert.AreEqual(exported.Count, exported.Distinct().Count(),
            "A resumable export must not emit the same row twice.");
    }

    /// <summary>Marks tests inconclusive when no container runtime is available.</summary>
    [TestInitialize]
    public void TestSetup()
    {
        if (DbApiFactory.DockerUnavailableReason is not null)
            Assert.Inconclusive(DbApiFactory.DockerUnavailableReason);
        if (DbApiFactory.StartupError is not null)
            Assert.Inconclusive($"SQL container failed to start: {DbApiFactory.StartupError}");
    }

    public TestContext TestContext { get; set; } = null!;
}
