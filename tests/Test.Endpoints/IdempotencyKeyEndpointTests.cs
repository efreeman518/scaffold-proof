using EF.Testing.Http;
using System.Net;
using System.Net.Http.Json;
using TaskFlow.Application.Models;
using Test.Support;

namespace Test.Endpoints;

/// <summary>
/// D-074 HTTP contract: an <c>Idempotency-Key</c> header on a create or child add, with no body id, maps to one
/// stored id, so a resent request replays the row it created instead of creating a second one. Run under both
/// application styles; the relational race and retention cases are in Test.Integration.
/// </summary>
[TestClass]
public class IdempotencyKeyEndpointTests
{
    private const string Header = "Idempotency-Key";
    private static EndpointStyleFixture _fixture = null!;

    /// <summary>Initializes shared test fixtures before the class-level test run begins.</summary>
    [ClassInitialize]
    public static void ClassInit(TestContext _) => _fixture = new EndpointStyleFixture();

    /// <summary>Disposes shared test fixtures after the class-level test run finishes.</summary>
    [ClassCleanup]
    public static void ClassCleanup() => _fixture?.Dispose();

    public TestContext TestContext { get; set; } = null!;

    /// <summary>The same key twice creates one task: 201, then a 200 replay of the stored row.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_SameKeyTwice_When_CreateTaskItem_Then_CreatesOneAndReplaysIt(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var key = NewKey();
        var title = $"Keyed-{Guid.NewGuid():N}";

        using var first = await PostAsync(client, "/api/v1/task-items", new TaskItemDto { Title = title }, key);
        using var second = await PostAsync(client, "/api/v1/task-items", new TaskItemDto { Title = title }, key);

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, "the resent request creates nothing, so it replays with 200");
        var created = await first.ItemAsync<TaskItemDto>(TestContext.CancellationToken);
        var replayed = await second.ItemAsync<TaskItemDto>(TestContext.CancellationToken);
        Assert.AreEqual(created!.Id, replayed!.Id);
        Assert.AreEqual(created.Version, replayed.Version, "a replay must not bump the stored version");
    }

    /// <summary>
    /// Category, Tag and Attachment creates honor the key the same way as a task create: the same key twice creates one
    /// row (201, then a 200 replay of it), and the key is scoped to its entity type. Before, the header was ignored and
    /// the resend created a second row.
    /// </summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service, "categories")]
    [DataRow(EndpointStyles.Cqrs, "categories")]
    [DataRow(EndpointStyles.Service, "tags")]
    [DataRow(EndpointStyles.Cqrs, "tags")]
    [DataRow(EndpointStyles.Service, "attachments")]
    [DataRow(EndpointStyles.Cqrs, "attachments")]
    [TestMethod]
    public async Task Given_SameKeyTwice_When_CreateCategoryTagOrAttachment_Then_CreatesOneAndReplaysIt(string style, string resource)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var key = NewKey();
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var ownerId = resource == "attachments" ? await CreateTaskAsync(client) : Guid.Empty;

        async Task<HttpResponseMessage> CreateAsync() => resource switch
        {
            "categories" => await PostAsync(client, "/api/v1/categories", new CategoryDto { Name = $"Cat-{suffix}" }, key),
            "tags" => await PostAsync(client, "/api/v1/tags", new TagDto { Name = $"Tag-{suffix}", Color = "#123456" }, key),
            _ => await PostAsync(client, "/api/v1/attachments", new AttachmentDto
            {
                FileName = $"{suffix}.pdf",
                ContentType = "application/pdf",
                FileSizeBytes = 1024,
                StorageUri = $"https://storage.example.com/{suffix}.pdf",
                OwnerType = TaskFlow.Domain.Shared.Enums.AttachmentOwnerType.TaskItem,
                OwnerId = ownerId
            }, key)
        };

        using var first = await CreateAsync();
        using var second = await CreateAsync();

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, await second.Content.ReadAsStringAsync(TestContext.CancellationToken));
        using var created = System.Text.Json.JsonDocument.Parse(await first.Content.ReadAsStringAsync(TestContext.CancellationToken));
        using var replayed = System.Text.Json.JsonDocument.Parse(await second.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual(
            created.RootElement.GetProperty("item").GetProperty("id").GetGuid(),
            replayed.RootElement.GetProperty("item").GetProperty("id").GetGuid());
    }

    /// <summary>A different key is a different logical request, so it creates a second task.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_DifferentKeys_When_CreateTaskItem_Then_CreatesTwo(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var title = $"Keyed-{Guid.NewGuid():N}";

        using var first = await PostAsync(client, "/api/v1/task-items", new TaskItemDto { Title = title }, NewKey());
        using var second = await PostAsync(client, "/api/v1/task-items", new TaskItemDto { Title = title }, NewKey());

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
        Assert.AreNotEqual(
            (await first.ItemAsync<TaskItemDto>(TestContext.CancellationToken))!.Id,
            (await second.ItemAsync<TaskItemDto>(TestContext.CancellationToken))!.Id);
    }

    /// <summary>A body id the caller supplied wins: the header is ignored and no mapping is stored for it.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_BodyId_When_CreateTaskItemWithKey_Then_BodyIdWinsAndTheKeyIsNotMapped(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var key = NewKey();
        var bodyId = Guid.CreateVersion7();

        using var withBodyId = await PostAsync(client, "/api/v1/task-items",
            new TaskItemDto { Id = bodyId, Title = $"Body-{Guid.NewGuid():N}" }, key);
        using var keyOnly = await PostAsync(client, "/api/v1/task-items",
            new TaskItemDto { Title = $"Key-{Guid.NewGuid():N}" }, key);

        Assert.AreEqual(HttpStatusCode.Created, withBodyId.StatusCode);
        Assert.AreEqual(bodyId, (await withBodyId.ItemAsync<TaskItemDto>(TestContext.CancellationToken))!.Id);
        Assert.AreEqual(HttpStatusCode.Created, keyOnly.StatusCode, "the key was ignored, so it maps to a new id");
        Assert.AreNotEqual(bodyId, (await keyOnly.ItemAsync<TaskItemDto>(TestContext.CancellationToken))!.Id);
    }

    /// <summary>
    /// An empty body id (<see cref="Guid.Empty"/>) is no id, so the header still maps: the same key twice creates one
    /// task. Before, the header was ignored and the create answered 400 (GR-17 rejects an empty caller id).
    /// </summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_EmptyBodyId_When_CreateTaskItemWithKeyTwice_Then_TheKeyMapsAndCreatesOne(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var key = NewKey();
        var title = $"EmptyId-{Guid.NewGuid():N}";

        using var first = await PostAsync(client, "/api/v1/task-items", new TaskItemDto { Id = Guid.Empty, Title = title }, key);
        using var second = await PostAsync(client, "/api/v1/task-items", new TaskItemDto { Id = Guid.Empty, Title = title }, key);

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, "the empty id is no id, so the key replays the first create");
        Assert.AreEqual(
            (await first.ItemAsync<TaskItemDto>(TestContext.CancellationToken))!.Id,
            (await second.ItemAsync<TaskItemDto>(TestContext.CancellationToken))!.Id);
    }

    /// <summary>
    /// The same rule on a child add: an empty body id is no id, so the key maps and the resend replays the first
    /// comment. Before, the header was ignored and the add answered 400 (an empty caller id is not a UUIDv7).
    /// </summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_EmptyBodyId_When_AddCommentWithKeyTwice_Then_AddsOne(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var taskId = await CreateTaskAsync(client);
        var key = NewKey();

        using var first = await PostAsync(client, $"/api/v1/task-items/{taskId}/comments", new CommentDto { Id = Guid.Empty, Body = "keyed" }, key);
        using var second = await PostAsync(client, $"/api/v1/task-items/{taskId}/comments", new CommentDto { Id = Guid.Empty, Body = "keyed" }, key);

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode, await second.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual(1, await CountAsync(client, $"/api/v1/task-items/{taskId}", "comments"));
    }

    /// <summary>
    /// A blank, oversized or repeated key is refused with a 400 ProblemDetails. HttpClient drops a header whose value
    /// is empty, so the blank value stands for the empty one: both fail the same non-empty rule.
    /// </summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service, "   ")]
    [DataRow(EndpointStyles.Cqrs, "   ")]
    [DataRow(EndpointStyles.Service, "201")]
    [DataRow(EndpointStyles.Cqrs, "201")]
    [DataRow(EndpointStyles.Service, "two")]
    [TestMethod]
    public async Task Given_InvalidKey_When_CreateTaskItem_Then_Returns400Problem(string style, string shape)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        using var request = Request("/api/v1/task-items", new TaskItemDto { Title = $"Invalid-{Guid.NewGuid():N}" });
        switch (shape)
        {
            case "201": request.Headers.TryAddWithoutValidation(Header, new string('k', 201)); break;
            case "two": request.Headers.TryAddWithoutValidation(Header, [NewKey(), NewKey()]); break;
            default: request.Headers.TryAddWithoutValidation(Header, shape); break;
        }

        using var response = await client.SendAsync(request, TestContext.CancellationToken);

        var body = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode, body);
        Assert.AreEqual("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("Idempotency-Key", body, StringComparison.Ordinal);
    }

    /// <summary>A key of exactly the maximum length is accepted.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [TestMethod]
    public async Task Given_MaxLengthKey_When_CreateTaskItem_Then_Created(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);

        using var response = await PostAsync(client, "/api/v1/task-items",
            new TaskItemDto { Title = $"Max-{Guid.NewGuid():N}" }, Guid.NewGuid().ToString("N").PadRight(200, 'k'));

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
    }

    /// <summary>The same key twice adds one comment; the same key on another task adds that task its own comment.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_SameKeyTwice_When_AddComment_Then_AddsOneAndScopesTheKeyToTheTask(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var taskId = await CreateTaskAsync(client);
        var otherTaskId = await CreateTaskAsync(client);
        var key = NewKey();

        using var first = await PostAsync(client, $"/api/v1/task-items/{taskId}/comments", new CommentDto { Body = "keyed comment" }, key);
        using var second = await PostAsync(client, $"/api/v1/task-items/{taskId}/comments", new CommentDto { Body = "keyed comment" }, key);
        using var other = await PostAsync(client, $"/api/v1/task-items/{otherTaskId}/comments", new CommentDto { Body = "keyed comment" }, key);

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, other.StatusCode, await other.Content.ReadAsStringAsync(TestContext.CancellationToken));
        var firstId = (await first.ItemAsync<CommentDto>(TestContext.CancellationToken))!.Id;
        Assert.AreEqual(firstId, (await second.ItemAsync<CommentDto>(TestContext.CancellationToken))!.Id);
        Assert.AreNotEqual(firstId, (await other.ItemAsync<CommentDto>(TestContext.CancellationToken))!.Id);
        Assert.AreEqual(1, await CountAsync(client, $"/api/v1/task-items/{taskId}", "comments"));
    }

    /// <summary>
    /// The child-add scope names the root by its parsed id, so one key sent with the task id in another accepted
    /// format (no hyphens, upper case, braces) is the same logical request and adds one comment.
    /// </summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_SameKeyWithTheTaskIdInOtherFormats_When_AddComment_Then_AddsOne(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var taskId = await CreateTaskAsync(client);
        var key = NewKey();

        using var first = await PostAsync(client, $"/api/v1/task-items/{taskId:D}/comments", new CommentDto { Body = "keyed" }, key);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync(TestContext.CancellationToken));
        var firstId = (await first.ItemAsync<CommentDto>(TestContext.CancellationToken))!.Id;
        foreach (var format in new[] { taskId.ToString("N"), taskId.ToString("D").ToUpperInvariant(), taskId.ToString("B") })
        {
            using var resend = await PostAsync(client, $"/api/v1/task-items/{format}/comments", new CommentDto { Body = "keyed" }, key);
            Assert.AreEqual(HttpStatusCode.OK, resend.StatusCode, $"{format}: {await resend.Content.ReadAsStringAsync(TestContext.CancellationToken)}");
            Assert.AreEqual(firstId, (await resend.ItemAsync<CommentDto>(TestContext.CancellationToken))!.Id, format);
        }

        Assert.AreEqual(1, await CountAsync(client, $"/api/v1/task-items/{taskId}", "comments"));
    }

    /// <summary>Different keys add two comments.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_DifferentKeys_When_AddComment_Then_AddsTwo(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var taskId = await CreateTaskAsync(client);

        using var first = await PostAsync(client, $"/api/v1/task-items/{taskId}/comments", new CommentDto { Body = "one" }, NewKey());
        using var second = await PostAsync(client, $"/api/v1/task-items/{taskId}/comments", new CommentDto { Body = "one" }, NewKey());

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        Assert.AreEqual(HttpStatusCode.Created, second.StatusCode);
        Assert.AreEqual(2, await CountAsync(client, $"/api/v1/task-items/{taskId}", "comments"));
    }

    /// <summary>The same key twice adds one checklist item.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_SameKeyTwice_When_AddChecklistItem_Then_AddsOne(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        var taskId = await CreateTaskAsync(client);
        var key = NewKey();

        using var first = await PostAsync(client, $"/api/v1/task-items/{taskId}/checklist-items",
            new ChecklistItemDto { Title = "keyed step", SortOrder = 1 }, key);
        using var second = await PostAsync(client, $"/api/v1/task-items/{taskId}/checklist-items",
            new ChecklistItemDto { Title = "keyed step", SortOrder = 1 }, key);

        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode, await first.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.AreEqual(HttpStatusCode.OK, second.StatusCode);
        Assert.AreEqual(
            (await first.ItemAsync<ChecklistItemDto>(TestContext.CancellationToken))!.Id,
            (await second.ItemAsync<ChecklistItemDto>(TestContext.CancellationToken))!.Id);
        Assert.AreEqual(1, await CountAsync(client, $"/api/v1/task-items/{taskId}", "checklistItems"));
    }

    private static string NewKey() => $"key-{Guid.NewGuid():N}";

    private static HttpRequestMessage Request<T>(string url, T item) =>
        new(HttpMethod.Post, url) { Content = JsonContent.Create(new DefaultRequest<T> { Item = item }) };

    private async Task<HttpResponseMessage> PostAsync<T>(HttpClient client, string url, T item, string key)
    {
        using var request = Request(url, item);
        request.Headers.TryAddWithoutValidation(Header, key);
        return await client.SendAsync(request, TestContext.CancellationToken);
    }

    private async Task<Guid> CreateTaskAsync(HttpClient client)
    {
        using var response = await client.PostAsJsonAsync("/api/v1/task-items",
            new DefaultRequest<TaskItemDto> { Item = new TaskItemDto { Title = $"Root-{Guid.NewGuid():N}" } },
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return (await response.ItemAsync<TaskItemDto>(TestContext.CancellationToken))!.Id!.Value;
    }

    // Counts a child collection on the aggregate GET, which is what a caller sees after the resend.
    private async Task<int> CountAsync(HttpClient client, string url, string collection)
    {
        using var response = await client.GetAsync(url, TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var json = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.CancellationToken));
        return json.RootElement.GetProperty("item").GetProperty(collection).GetArrayLength();
    }
}
