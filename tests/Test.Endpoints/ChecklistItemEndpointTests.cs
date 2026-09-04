using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Enums;

namespace Test.Endpoints;

/// <summary>
/// HTTP contract tests for <c>/api/v1/checklist-items</c> CRUD; each test seeds a parent TaskItem via the
/// real API surface to satisfy the foreign-key relation.
/// Endpoint tier (WebApplicationFactory + EF InMemory via <c>CustomApiFactory</c>): contract-level
/// coverage of routing, status codes, and envelope shape - not FK cascade semantics.
/// </summary>
[TestClass]
public class ChecklistItemEndpointTests
{
    private static EndpointStyleFixture _fixture = null!;
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    /// <summary>Initializes shared test fixtures before the class-level test run begins.</summary>
    [ClassInitialize]
    public static void ClassInit(TestContext _) => _fixture = new EndpointStyleFixture();

    /// <summary>Disposes shared test fixtures after the class-level test run finishes.</summary>
    [ClassCleanup]
    public static void ClassCleanup() => _fixture?.Dispose();

    /// <summary>Creates client used by the surrounding test cases.</summary>
    private static HttpClient CreateClient(string style) => _fixture.CreateClient(style);

    /// <summary>Creates parent task item used by the surrounding test cases.</summary>
    private async Task<Guid> CreateParentTaskItem(HttpClient client)
    {
        var dto = new TaskItemDto { Title = "ParentForChecklist", Priority = Priority.Medium };
        var response = await client.PostAsJsonAsync("/api/v1/task-items", new DefaultRequest<TaskItemDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        var created = (await response.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;
        return created!.Id!.Value;
    }

    // ChecklistItems are internal to the TaskItem aggregate (GR-15): created, updated, and removed
    // only through the nested /task-items/{id}/checklist-items routes on the root. Reads still live
    // on /checklist-items.

    /// <summary>Verifies that given non existent ID, when get checklist item, then returns 404.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_NonExistentId_When_GetChecklistItem_Then_Returns404(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);

        var response = await client.GetAsync($"/api/v1/checklist-items/{Guid.NewGuid()}", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Verifies that adding a checklist item through the TaskItem root returns 201 and is readable.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ValidPayload_When_AddChecklistItemToTaskItem_Then_Returns201AndReadable(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var taskId = await CreateParentTaskItem(client);

        var dto = new ChecklistItemDto { Title = "Nested step", SortOrder = 1, IsCompleted = false, TaskItemId = taskId };
        var addResp = await client.PostAsJsonAsync($"/api/v1/task-items/{taskId}/checklist-items",
            new DefaultRequest<ChecklistItemDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, addResp.StatusCode,
            $"Add failed: {await addResp.Content.ReadAsStringAsync(TestContext.CancellationToken)}");

        var created = (await addResp.Content.ReadFromJsonAsync<DefaultResponse<ChecklistItemDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;
        Assert.IsNotNull(created);
        Assert.AreEqual("Nested step", created.Title);

        var getResp = await client.GetAsync($"/api/v1/checklist-items/{created.Id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, getResp.StatusCode);
    }

    /// <summary>Verifies the add/update/remove checklist-item lifecycle through the TaskItem root.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ChecklistItem_When_UpdatedAndRemovedThroughRoot_Then_ReflectsState(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var taskId = await CreateParentTaskItem(client);

        var addResp = await client.PostAsJsonAsync($"/api/v1/task-items/{taskId}/checklist-items",
            new DefaultRequest<ChecklistItemDto> { Item = new ChecklistItemDto { Title = "Step", SortOrder = 1, TaskItemId = taskId } }, cancellationToken: TestContext.CancellationToken);
        var itemId = (await addResp.Content.ReadFromJsonAsync<DefaultResponse<ChecklistItemDto>>(_jsonOptions, TestContext.CancellationToken))!.Item!.Id!.Value;

        // Child writes carry the ROOT ETag (D-031).
        var rootETag = addResp.ETagValue();
        Assert.IsNotNull(rootETag, "A child add must return the new root aggregate ETag.");

        var updResp = await client.PutWithIfMatchAsync($"/api/v1/task-items/{taskId}/checklist-items/{itemId}",
            new DefaultRequest<ChecklistItemDto> { Item = new ChecklistItemDto { Title = "Step done", IsCompleted = true, SortOrder = 1, TaskItemId = taskId } }, $"\"{rootETag}\"", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, updResp.StatusCode);
        var updated = (await updResp.Content.ReadFromJsonAsync<DefaultResponse<ChecklistItemDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;
        Assert.IsTrue(updated!.IsCompleted);

        var delResp = await client.DeleteWithIfMatchAsync($"/api/v1/task-items/{taskId}/checklist-items/{itemId}", $"\"{updResp.ETagValue()}\"", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, delResp.StatusCode);

        var getResp = await client.GetAsync($"/api/v1/checklist-items/{itemId}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, getResp.StatusCode);
    }

    public TestContext TestContext { get; set; } = null!;
}
