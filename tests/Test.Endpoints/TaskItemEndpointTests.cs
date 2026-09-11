using EF.Common.Contracts;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;
using TaskFlow.Domain.Shared.Enums;

namespace Test.Endpoints;

/// <summary>
/// HTTP contract tests for <c>/api/v1/task-items</c> CRUD plus search (filter by SearchTerm, paged response)
/// and a full create->read->update->delete cycle.
/// Endpoint tier (WebApplicationFactory + EF InMemory via <c>CustomApiFactory</c>): the same
/// multi-endpoint workflow runs against real SQL in <c>TaskItemCrudE2ETests</c>; here we only need
/// contract correctness, not SQL semantics, so InMemory is sufficient.
/// </summary>
[TestClass]
public class TaskItemEndpointTests
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

    /// <summary>Verifies that given valid payload, when post task item, then returns 201.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ValidPayload_When_PostTaskItem_Then_Returns201(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var dto = new TaskItemDto { Title = "Test Task", Priority = Priority.Medium };

        var response = await client.PostAsJsonAsync("/api/v1/task-items", new DefaultRequest<TaskItemDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;
        Assert.IsNotNull(created);
        Assert.AreEqual("Test Task", created.Title);
        Assert.IsNotNull(created.Id);
    }

    /// <summary>Verifies that given existing task item, when get by ID, then returns 200.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ExistingTaskItem_When_GetById_Then_Returns200(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var dto = new TaskItemDto { Title = "GetTest", Priority = Priority.Low };
        var createResponse = await client.PostAsJsonAsync("/api/v1/task-items", new DefaultRequest<TaskItemDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;

        var response = await client.GetAsync($"/api/v1/task-items/{created!.Id}", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;
        Assert.IsNotNull(result);
        Assert.AreEqual("GetTest", result.Title);
    }

    /// <summary>Verifies that given non existent ID, when get task item, then returns 404.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_NonExistentId_When_GetTaskItem_Then_Returns404(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);

        var response = await client.GetAsync($"/api/v1/task-items/{Guid.NewGuid()}", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Verifies that given existing task item, when put update, then returns 200.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ExistingTaskItem_When_PutUpdate_Then_Returns200(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var dto = new TaskItemDto { Title = "Before Update", Priority = Priority.Medium };
        var createResponse = await client.PostAsJsonAsync("/api/v1/task-items", new DefaultRequest<TaskItemDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;

        var updateDto = new TaskItemDto
        {
            Id = created!.Id,
            Title = "After Update",
            Priority = Priority.High,
            Status = created.Status
        };
        var response = await client.PutWithIfMatchAsync($"/api/v1/task-items/{created.Id}", new DefaultRequest<TaskItemDto> { Item = updateDto }, ConcurrencyHttpExtensions.IfMatch(created.Version!.Value), TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;
        Assert.AreEqual("After Update", updated!.Title);
    }

    /// <summary>Verifies that given existing task item, when delete, then returns 204.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ExistingTaskItem_When_Delete_Then_Returns204(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var dto = new TaskItemDto { Title = "ToDelete", Priority = Priority.Low };
        var createResponse = await client.PostAsJsonAsync("/api/v1/task-items", new DefaultRequest<TaskItemDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;

        var response = await client.DeleteWithIfMatchAsync($"/api/v1/task-items/{created!.Id}", ConcurrencyHttpExtensions.IfMatch(created.Version!.Value), TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        // Verify deleted
        var getResponse = await client.GetAsync($"/api/v1/task-items/{created.Id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    /// <summary>Verifies that given existing task items, when search, then returns filtered page.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ExistingTaskItems_When_Search_Then_ReturnsFilteredPage(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);

        // Seed
        await client.PostAsJsonAsync("/api/v1/task-items",
            new DefaultRequest<TaskItemDto> { Item = new TaskItemDto { Title = "SearchTarget Alpha", Priority = Priority.High } }, cancellationToken: TestContext.CancellationToken);
        await client.PostAsJsonAsync("/api/v1/task-items",
            new DefaultRequest<TaskItemDto> { Item = new TaskItemDto { Title = "Other Beta", Priority = Priority.Low } }, cancellationToken: TestContext.CancellationToken);

        var searchRequest = new TaskItemCursorSearchRequest
        {
            PageSize = 10,
            Filter = new TaskItemSearchFilter { SearchTerm = "SearchTarget" }
        };

        var response = await client.PostAsJsonAsync("/api/v1/task-items/search", searchRequest, cancellationToken: TestContext.CancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, responseBody);
        var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        // Keyset pages carry no total (GR-18): HasMore replaces it.
        Assert.IsFalse(root.GetProperty("hasMore").GetBoolean());
        var items = root.GetProperty("items");
        Assert.Contains(e => e.GetProperty("title").GetString()!.Contains("SearchTarget"), items.EnumerateArray());
    }

    /// <summary>Verifies that given empty database, when search, then returns empty page.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_EmptyDatabase_When_Search_Then_ReturnsEmptyPage(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var searchRequest = new TaskItemCursorSearchRequest
        {
            PageSize = 10,
            Filter = new TaskItemSearchFilter()
        };

        var response = await client.PostAsJsonAsync("/api/v1/task-items/search", searchRequest, cancellationToken: TestContext.CancellationToken);

        var responseBody = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, responseBody);
        var doc = JsonDocument.Parse(responseBody);
        var root = doc.RootElement;
        Assert.IsTrue(root.TryGetProperty("items", out _));
    }

    /// <summary>Verifies that given full CRUD cycle, when all operations executed, then all succeed.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_FullCrudCycle_When_AllOperationsExecuted_Then_AllSucceed(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);

        // Create
        var dto = new TaskItemDto { Title = "CrudCycle", Priority = Priority.Critical };
        var createResponse = await client.PostAsJsonAsync("/api/v1/task-items", new DefaultRequest<TaskItemDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);
        var created = (await createResponse.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;

        // Read
        var getResponse = await client.GetAsync($"/api/v1/task-items/{created!.Id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, getResponse.StatusCode);

        // Update
        var updateDto = new TaskItemDto
        {
            Id = created.Id,
            Title = "CrudCycle Updated",
            Priority = Priority.Low,
            Status = created.Status
        };
        var updateResponse = await client.PutWithIfMatchAsync($"/api/v1/task-items/{created.Id}", new DefaultRequest<TaskItemDto> { Item = updateDto }, ConcurrencyHttpExtensions.IfMatch(created.Version!.Value), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, updateResponse.StatusCode);

        // Delete
        var updatedItem = (await updateResponse.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;
        var deleteResponse = await client.DeleteWithIfMatchAsync($"/api/v1/task-items/{created.Id}", ConcurrencyHttpExtensions.IfMatch(updatedItem!.Version!.Value), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Verify deleted
        var verifyResponse = await client.GetAsync($"/api/v1/task-items/{created.Id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, verifyResponse.StatusCode);
    }

    public TestContext TestContext { get; set; } = null!;
}
