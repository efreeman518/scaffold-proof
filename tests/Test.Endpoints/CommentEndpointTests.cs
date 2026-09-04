using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Enums;

namespace Test.Endpoints;

/// <summary>
/// HTTP contract tests for <c>/api/v1/comments</c> CRUD; each test seeds a parent TaskItem first.
/// Endpoint tier (WebApplicationFactory + EF InMemory via <c>CustomApiFactory</c>): contract-level
/// coverage - status codes, envelope shape, and 404 paths.
/// </summary>
[TestClass]
public class CommentEndpointTests
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
        var dto = new TaskItemDto { Title = "ParentForComment", Priority = Priority.Medium };
        var response = await client.PostAsJsonAsync("/api/v1/task-items", new DefaultRequest<TaskItemDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        var created = (await response.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;
        return created!.Id!.Value;
    }

    // Comments are internal to the TaskItem aggregate (GR-15): they are created, updated, and removed
    // only through the nested /task-items/{id}/comments routes on the root, never a standalone
    // /comments write route. Reads still live on /comments.

    /// <summary>Verifies that given non existent ID, when get comment, then returns 404.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_NonExistentId_When_GetComment_Then_Returns404(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);

        var response = await client.GetAsync($"/api/v1/comments/{Guid.NewGuid()}", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Verifies that adding a comment through the TaskItem root returns 201 and is readable.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ValidPayload_When_AddCommentToTaskItem_Then_Returns201AndReadable(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var taskId = await CreateParentTaskItem(client);

        var dto = new CommentDto { Body = "Nested add", TaskItemId = taskId };
        var addResp = await client.PostAsJsonAsync($"/api/v1/task-items/{taskId}/comments",
            new DefaultRequest<CommentDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, addResp.StatusCode,
            $"Add failed: {await addResp.Content.ReadAsStringAsync(TestContext.CancellationToken)}");

        var created = (await addResp.Content.ReadFromJsonAsync<DefaultResponse<CommentDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;
        Assert.IsNotNull(created);
        Assert.AreEqual("Nested add", created.Body);

        var getResp = await client.GetAsync($"/api/v1/comments/{created.Id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, getResp.StatusCode);
    }

    /// <summary>Verifies the add/update/remove comment lifecycle through the TaskItem root.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_Comment_When_UpdatedAndRemovedThroughRoot_Then_ReflectsState(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var taskId = await CreateParentTaskItem(client);

        var addResp = await client.PostAsJsonAsync($"/api/v1/task-items/{taskId}/comments",
            new DefaultRequest<CommentDto> { Item = new CommentDto { Body = "Original", TaskItemId = taskId } }, cancellationToken: TestContext.CancellationToken);
        var commentId = (await addResp.Content.ReadFromJsonAsync<DefaultResponse<CommentDto>>(_jsonOptions, TestContext.CancellationToken))!.Item!.Id!.Value;

        // Child writes carry the ROOT ETag (D-031); the add response already returns the bumped root version.
        var rootETag = addResp.ETagValue();
        Assert.IsNotNull(rootETag, "A child add must return the new root aggregate ETag.");

        var updResp = await client.PutWithIfMatchAsync($"/api/v1/task-items/{taskId}/comments/{commentId}",
            new DefaultRequest<CommentDto> { Item = new CommentDto { Body = "Edited", TaskItemId = taskId } }, $"\"{rootETag}\"", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, updResp.StatusCode);
        var updated = (await updResp.Content.ReadFromJsonAsync<DefaultResponse<CommentDto>>(_jsonOptions, TestContext.CancellationToken))!.Item;
        Assert.AreEqual("Edited", updated!.Body);

        var delResp = await client.DeleteWithIfMatchAsync($"/api/v1/task-items/{taskId}/comments/{commentId}", $"\"{updResp.ETagValue()}\"", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, delResp.StatusCode);

        var getResp = await client.GetAsync($"/api/v1/comments/{commentId}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, getResp.StatusCode);
    }

    /// <summary>Verifies that adding a comment to a missing TaskItem returns 404.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_MissingTaskItem_When_AddComment_Then_Returns404(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);

        var response = await client.PostAsJsonAsync($"/api/v1/task-items/{Guid.NewGuid()}/comments",
            new DefaultRequest<CommentDto> { Item = new CommentDto { Body = "Orphan" } }, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    public TestContext TestContext { get; set; } = null!;
}
