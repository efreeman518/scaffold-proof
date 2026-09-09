using EF.Common.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Domain.Shared.Enums;
using Test.Support;

namespace Test.E2E;

/// <summary>
/// Multi-endpoint workflow tests over the full HTTP->Endpoint->Service->EF->SQL stack: TaskItem/Category/Tag
/// CRUD round-trips, server-side paged search across distinct pages, and child-aggregate (Comment,
/// ChecklistItem) lifecycles.
/// SQL tier (WebApplicationFactory + Testcontainers SQL via <c>DbApiFactory</c>): real SQL is required
/// for paging plans, FK constraints applied by EF migrations, and projection behavior - InMemory
/// (Test.Endpoints tier) would silently mask these. The Aspire tier is unnecessary because only one
/// backing service (SQL) participates.
/// </summary>
[TestClass]
[TestCategory("E2E")]
public class TaskItemCrudE2ETests
{
    private static DbApiFactory _factory = null!;
    private static readonly JsonSerializerOptions _json = JsonTestOptions.Default;

    /// <summary>Initializes shared test fixtures before the class-level test run begins.</summary>
    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        await DbApiFactory.StartContainerAsync(_.CancellationToken);
        if (DbApiFactory.DockerUnavailableReason is not null || DbApiFactory.StartupError is not null)
            return;

        _factory = new DbApiFactory();

        // Apply EF migrations against the real SQL container
        using var scope = _factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaskFlow.Infrastructure.Data.TaskFlowDbContextTrxn>();
        await db.Database.MigrateAsync(_.CancellationToken);
    }

    /// <summary>Disposes shared test fixtures after the class-level test run finishes.</summary>
    [ClassCleanup]
    public static void ClassCleanup() => _factory?.Dispose();

    /// <summary>Creates client used by the surrounding test cases.</summary>
    private static HttpClient CreateClient() => _factory.CreateClient();

    // -- TaskItem full CRUD ------------------------------------

    [TestMethod]
    public async Task TaskItem_FullCrudCycle_AgainstRealSql()
    {
        using var client = CreateClient();

        // CREATE
        var dto = new TaskItemDto { Title = "E2E Task", Priority = Priority.High };
        var createResp = await client.PostAsJsonAsync("/api/v1/task-items",
            new DefaultRequest<TaskItemDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, createResp.StatusCode,
            $"Create failed: {await createResp.Content.ReadAsStringAsync(TestContext.CancellationToken)}");
        var created = (await createResp.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_json, TestContext.CancellationToken))!.Item;
        Assert.IsNotNull(created);
        Assert.AreEqual("E2E Task", created.Title);
        var id = created.Id!.Value;

        // READ
        var getResp = await client.GetAsync($"/api/v1/task-items/{id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, getResp.StatusCode);
        var fetched = (await getResp.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_json, TestContext.CancellationToken))!.Item;
        Assert.AreEqual("E2E Task", fetched!.Title);

        // UPDATE
        var updateDto = new TaskItemDto
        {
            Id = id,
            Title = "E2E Task Updated",
            Priority = Priority.Low,
            Status = fetched.Status
        };
        var putResp = await client.PutWithIfMatchAsync($"/api/v1/task-items/{id}",
            new DefaultRequest<TaskItemDto> { Item = updateDto },
            ConcurrencyHttp.IfMatch(fetched.Version!.Value), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, putResp.StatusCode,
            $"Update failed: {await putResp.Content.ReadAsStringAsync(TestContext.CancellationToken)}");
        var updated = (await putResp.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_json, TestContext.CancellationToken))!.Item;
        Assert.AreEqual("E2E Task Updated", updated!.Title);

        // DELETE
        var delResp = await client.DeleteWithIfMatchAsync($"/api/v1/task-items/{id}",
            ConcurrencyHttp.IfMatch(updated.Version!.Value), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, delResp.StatusCode);

        // VERIFY DELETED
        var verifyResp = await client.GetAsync($"/api/v1/task-items/{id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, verifyResp.StatusCode);
    }

    // -- Category full CRUD ------------------------------------

    [TestMethod]
    public async Task Category_FullCrudCycle_AgainstRealSql()
    {
        using var client = CreateClient();

        var dto = new CategoryDto { Name = "E2E Category", IsActive = true, SortOrder = 1 };
        var createResp = await client.PostAsJsonAsync("/api/v1/categories",
            new DefaultRequest<CategoryDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, createResp.StatusCode,
            $"Create failed: {await createResp.Content.ReadAsStringAsync(TestContext.CancellationToken)}");
        var created = (await createResp.Content.ReadFromJsonAsync<DefaultResponse<CategoryDto>>(_json, TestContext.CancellationToken))!.Item;
        Assert.IsNotNull(created);
        var id = created.Id!.Value;

        var getResp = await client.GetAsync($"/api/v1/categories/{id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, getResp.StatusCode);

        var updateDto = new CategoryDto { Id = id, Name = "E2E Category Updated", IsActive = true, SortOrder = 2 };
        var putResp = await client.PutWithIfMatchAsync($"/api/v1/categories/{id}",
            new DefaultRequest<CategoryDto> { Item = updateDto },
            ConcurrencyHttp.IfMatch(created.Version!.Value), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, putResp.StatusCode);

        var delResp = await client.DeleteWithIfMatchAsync($"/api/v1/categories/{id}",
            ConcurrencyHttp.IfMatch(putResp.ETagValue()), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, delResp.StatusCode);

        var verifyResp = await client.GetAsync($"/api/v1/categories/{id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, verifyResp.StatusCode);
    }

    // -- Tag full CRUD -----------------------------------------

    [TestMethod]
    public async Task Tag_FullCrudCycle_AgainstRealSql()
    {
        using var client = CreateClient();

        var dto = new TagDto { Name = "e2e-tag", Color = "#FF0000" };
        var createResp = await client.PostAsJsonAsync("/api/v1/tags",
            new DefaultRequest<TagDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, createResp.StatusCode,
            $"Create failed: {await createResp.Content.ReadAsStringAsync(TestContext.CancellationToken)}");
        var created = (await createResp.Content.ReadFromJsonAsync<DefaultResponse<TagDto>>(_json, TestContext.CancellationToken))!.Item;
        Assert.IsNotNull(created);
        var id = created.Id!.Value;

        var getResp = await client.GetAsync($"/api/v1/tags/{id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, getResp.StatusCode);

        var updateDto = new TagDto { Id = id, Name = "e2e-tag-updated", Color = "#00FF00" };
        var putResp = await client.PutWithIfMatchAsync($"/api/v1/tags/{id}",
            new DefaultRequest<TagDto> { Item = updateDto },
            ConcurrencyHttp.IfMatch(created.Version!.Value), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, putResp.StatusCode);

        var delResp = await client.DeleteWithIfMatchAsync($"/api/v1/tags/{id}",
            ConcurrencyHttp.IfMatch(putResp.ETagValue()), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, delResp.StatusCode);

        var verifyResp = await client.GetAsync($"/api/v1/tags/{id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, verifyResp.StatusCode);
    }

    // -- Search works against real SQL -------------------------

    [TestMethod]
    public async Task TaskItem_Search_ReturnsResults_AgainstRealSql()
    {
        using var client = CreateClient();

        // Seed a task
        var searchMarker = $"Searchable E2E {Guid.NewGuid():N}";
        var dto = new TaskItemDto { Title = $"{searchMarker} Task", Priority = Priority.Medium };
        await client.PostAsJsonAsync("/api/v1/task-items",
            new DefaultRequest<TaskItemDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);

        // Search
        var searchReq = new TaskItemCursorSearchRequest
        {
            PageSize = 50,
            Filter = new TaskItemSearchFilter { SearchTerm = searchMarker }
        };
        var searchResp = await client.PostAsJsonAsync("/api/v1/task-items/search", searchReq, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, searchResp.StatusCode);

        using var document = await JsonDocument.ParseAsync(await searchResp.Content.ReadAsStreamAsync(TestContext.CancellationToken), cancellationToken: TestContext.CancellationToken);
        var root = document.RootElement;
        var titles = root.GetProperty("data")
            .EnumerateArray()
            .Select(item => item.GetProperty("title").GetString())
            .Where(title => !string.IsNullOrWhiteSpace(title))
            .Cast<string>()
            .ToList();

        // Keyset pages carry no total (GR-18) - the rows themselves are the assertion.
        CollectionAssert.Contains(titles, $"{searchMarker} Task");
    }

    /// <summary>
    /// Walks every keyset page against real SQL. Offset paging is gone (GR-18), so this replaces the
    /// former distinct-pages assertion: the property that matters now is that a page-through returns
    /// each row exactly once, which is precisely what a non-total sort order would break.
    /// </summary>
    [TestMethod]
    public async Task TaskItem_CursorSearch_WalksEveryRowOnce_AgainstRealSql()
    {
        using var client = CreateClient();

        var searchMarker = $"Paged Search E2E {Guid.NewGuid():N}";
        foreach (var title in new[] { $"{searchMarker} 01", $"{searchMarker} 02", $"{searchMarker} 03" })
        {
            var dto = new TaskItemDto { Title = title, Priority = Priority.Medium };
            var createResp = await client.PostAsJsonAsync("/api/v1/task-items",
                new DefaultRequest<TaskItemDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.Created, createResp.StatusCode,
                $"Create failed: {await createResp.Content.ReadAsStringAsync(TestContext.CancellationToken)}");
        }

        async Task<(bool HasMore, string? NextCursor, List<string> Titles)> SearchPageAsync(string? cursor)
        {
            var request = new TaskItemCursorSearchRequest
            {
                PageSize = 1,
                SortMode = TaskItemSortMode.IdAsc,
                Cursor = cursor,
                Filter = new TaskItemSearchFilter { SearchTerm = searchMarker }
            };

            var response = await client.PostAsJsonAsync("/api/v1/task-items/search", request, cancellationToken: TestContext.CancellationToken);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode,
                $"Search failed: {await response.Content.ReadAsStringAsync(TestContext.CancellationToken)}");

            using var document = await JsonDocument.ParseAsync(
                await response.Content.ReadAsStreamAsync(TestContext.CancellationToken),
                cancellationToken: TestContext.CancellationToken);
            var root = document.RootElement;
            var titles = root.GetProperty("data")
                .EnumerateArray()
                .Select(item => item.GetProperty("title").GetString())
                .Where(title => !string.IsNullOrWhiteSpace(title))
                .Cast<string>()
                .ToList();

            var next = root.GetProperty("nextCursor").ValueKind == JsonValueKind.Null
                ? null
                : root.GetProperty("nextCursor").GetString();

            return (root.GetProperty("hasMore").GetBoolean(), next, titles);
        }

        var seen = new List<string>();
        string? cursor = null;
        for (var page = 0; page < 5; page++)
        {
            var (hasMore, next, titles) = await SearchPageAsync(cursor);
            seen.AddRange(titles);
            if (!hasMore)
            {
                Assert.IsNull(next, "The final page must not hand out a cursor.");
                break;
            }

            Assert.IsNotNull(next, "A page with HasMore must carry the next cursor.");
            cursor = next;
        }

        CollectionAssert.AreEquivalent(
            new[] { $"{searchMarker} 01", $"{searchMarker} 02", $"{searchMarker} 03" },
            seen);
        Assert.AreEqual(seen.Count, seen.Distinct().Count(), "Keyset paging must not repeat a row.");
    }

    // -- Comment CRUD (child of TaskItem) ----------------------

    [TestMethod]
    public async Task Comment_CrudCycle_AgainstRealSql()
    {
        using var client = CreateClient();

        // Create parent task
        var taskDto = new TaskItemDto { Title = "Parent for Comment E2E", Priority = Priority.Low };
        var taskResp = await client.PostAsJsonAsync("/api/v1/task-items",
            new DefaultRequest<TaskItemDto> { Item = taskDto }, cancellationToken: TestContext.CancellationToken);
        var taskId = (await taskResp.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_json, TestContext.CancellationToken))!.Item!.Id!.Value;

        // Add comment through the aggregate root (nested route - GR-15)
        var commentDto = new CommentDto { Body = "E2E Comment", TaskItemId = taskId };
        var createResp = await client.PostAsJsonAsync($"/api/v1/task-items/{taskId}/comments",
            new DefaultRequest<CommentDto> { Item = commentDto }, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, createResp.StatusCode,
            $"Create failed: {await createResp.Content.ReadAsStringAsync(TestContext.CancellationToken)}");
        var created = (await createResp.Content.ReadFromJsonAsync<DefaultResponse<CommentDto>>(_json, TestContext.CancellationToken))!.Item;
        Assert.IsNotNull(created);
        var commentId = created.Id!.Value;

        // Read (child read endpoint is retained)
        var getResp = await client.GetAsync($"/api/v1/comments/{commentId}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, getResp.StatusCode);

        // Remove through the aggregate root; the orphaned row is hard-deleted
        var delResp = await client.DeleteWithIfMatchAsync(
            $"/api/v1/task-items/{taskId}/comments/{commentId}",
            ConcurrencyHttp.IfMatch(createResp.ETagValue()), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, delResp.StatusCode);

        var verifyResp = await client.GetAsync($"/api/v1/comments/{commentId}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, verifyResp.StatusCode);
    }

    // -- ChecklistItem CRUD (child of TaskItem) ----------------

    [TestMethod]
    public async Task ChecklistItem_CrudCycle_AgainstRealSql()
    {
        using var client = CreateClient();

        // Create parent task
        var taskDto = new TaskItemDto { Title = "Parent for Checklist E2E", Priority = Priority.Low };
        var taskResp = await client.PostAsJsonAsync("/api/v1/task-items",
            new DefaultRequest<TaskItemDto> { Item = taskDto }, cancellationToken: TestContext.CancellationToken);
        var taskId = (await taskResp.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_json, TestContext.CancellationToken))!.Item!.Id!.Value;

        // Add checklist item through the aggregate root (nested route - GR-15)
        var dto = new ChecklistItemDto { Title = "E2E Checklist Item", IsCompleted = false, SortOrder = 1, TaskItemId = taskId };
        var createResp = await client.PostAsJsonAsync($"/api/v1/task-items/{taskId}/checklist-items",
            new DefaultRequest<ChecklistItemDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, createResp.StatusCode,
            $"Create failed: {await createResp.Content.ReadAsStringAsync(TestContext.CancellationToken)}");
        var created = (await createResp.Content.ReadFromJsonAsync<DefaultResponse<ChecklistItemDto>>(_json, TestContext.CancellationToken))!.Item;
        Assert.IsNotNull(created);

        // Remove through the aggregate root
        var delResp = await client.DeleteWithIfMatchAsync(
            $"/api/v1/task-items/{taskId}/checklist-items/{created.Id}",
            ConcurrencyHttp.IfMatch(createResp.ETagValue()), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, delResp.StatusCode);
    }

    [TestInitialize]
    public void TestSetup()
    {
        if (DbApiFactory.DockerUnavailableReason is not null)
        {
            Assert.Inconclusive(DbApiFactory.DockerUnavailableReason);
            return;
        }

        if (DbApiFactory.StartupError is not null)
            Assert.Fail($"SQL container startup failed after Docker preflight succeeded:{Environment.NewLine}{DbApiFactory.StartupError}");
    }

    public TestContext TestContext { get; set; } = null!;
}
