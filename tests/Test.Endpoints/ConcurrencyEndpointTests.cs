using System.Net;
using System.Net.Http.Json;
using TaskFlow.Application.Models;

namespace Test.Endpoints;

/// <summary>
/// HTTP contract tests for the concurrency and idempotent-create rules (GR-16, GR-17, D-031..D-033),
/// run under both application styles. These are the cases that decide whether a lost update is
/// prevented or silently accepted, so each one asserts a specific status code, not just "not 200".
/// </summary>
[TestClass]
public class ConcurrencyEndpointTests
{
    private static EndpointStyleFixture _fixture = null!;

    /// <summary>Initializes shared test fixtures before the class-level test run begins.</summary>
    [ClassInitialize]
    public static void ClassInit(TestContext _) => _fixture = new EndpointStyleFixture();

    /// <summary>Disposes shared test fixtures after the class-level test run finishes.</summary>
    [ClassCleanup]
    public static void ClassCleanup() => _fixture?.Dispose();

    /// <summary>Creates client used by the surrounding test cases.</summary>
    private static HttpClient CreateClient(string style) => _fixture.CreateClient(style);

    /// <summary>Creates a category and returns it with its current version.</summary>
    private async Task<CategoryDto> CreateCategoryAsync(HttpClient client, string name)
    {
        var response = await client.PostAsJsonAsync("/api/v1/categories",
            new DefaultRequest<CategoryDto> { Item = new CategoryDto { Name = name, IsActive = true, SortOrder = 1 } },
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return (await response.ItemAsync<CategoryDto>(TestContext.CancellationToken))!;
    }

    /// <summary>Verifies a create response carries the ETag a later write must echo.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_Create_When_Responded_Then_CarriesETagMatchingVersion(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);

        var response = await client.PostAsJsonAsync("/api/v1/categories",
            new DefaultRequest<CategoryDto> { Item = new CategoryDto { Name = $"ETag-{Guid.NewGuid():N}", IsActive = true } },
            cancellationToken: TestContext.CancellationToken);

        var created = await response.ItemAsync<CategoryDto>(TestContext.CancellationToken);
        Assert.AreEqual(created!.Version!.Value.ToString(), response.ETagValue());
    }

    /// <summary>Verifies a GET carries the ETag, which is the only way a client can obtain one to write with.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ExistingEntity_When_Get_Then_CarriesETag(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var created = await CreateCategoryAsync(client, $"GetETag-{Guid.NewGuid():N}");

        using var response = await client.GetAsync($"/api/v1/categories/{created.Id}", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual(created.Version!.Value.ToString(), response.ETagValue());
    }

    /// <summary>Verifies a write without If-Match is refused with 428, not silently applied.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_MissingIfMatch_When_Put_Then_Returns428(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var created = await CreateCategoryAsync(client, $"Missing-{Guid.NewGuid():N}");

        using var response = await client.PutWithIfMatchAsync(
            $"/api/v1/categories/{created.Id}",
            new DefaultRequest<CategoryDto> { Item = created with { Name = "Changed" } },
            ifMatch: null,
            TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    /// <summary>Verifies a delete without If-Match is refused with 428.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_MissingIfMatch_When_Delete_Then_Returns428(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var created = await CreateCategoryAsync(client, $"MissingDel-{Guid.NewGuid():N}");

        using var response = await client.DeleteWithIfMatchAsync(
            $"/api/v1/categories/{created.Id}", ifMatch: null, TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.PreconditionRequired, response.StatusCode);
    }

    /// <summary>Verifies a weak entity tag is a 400: it cannot express an exact-version precondition.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_WeakIfMatch_When_Put_Then_Returns400(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var created = await CreateCategoryAsync(client, $"Weak-{Guid.NewGuid():N}");

        using var response = await client.PutWithIfMatchAsync(
            $"/api/v1/categories/{created.Id}",
            new DefaultRequest<CategoryDto> { Item = created with { Name = "Changed" } },
            $"W/\"{created.Version}\"",
            TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Verifies an unparseable If-Match is a 400 rather than being treated as "no precondition".</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_MalformedIfMatch_When_Put_Then_Returns400(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var created = await CreateCategoryAsync(client, $"Malformed-{Guid.NewGuid():N}");

        using var response = await client.PutWithIfMatchAsync(
            $"/api/v1/categories/{created.Id}",
            new DefaultRequest<CategoryDto> { Item = created with { Name = "Changed" } },
            "\"not-a-version\"",
            TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Verifies a stale version is refused with 412 and the current version comes back as the ETag.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_StaleIfMatch_When_Put_Then_Returns412WithCurrentETag(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var created = await CreateCategoryAsync(client, $"Stale-{Guid.NewGuid():N}");

        // First writer wins and moves the version on.
        using var first = await client.PutWithIfMatchAsync(
            $"/api/v1/categories/{created.Id}",
            new DefaultRequest<CategoryDto> { Item = created with { Name = "First" } },
            ConcurrencyHttpExtensions.IfMatch(created.Version!.Value),
            TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, first.StatusCode);
        var currentVersion = (await first.ItemAsync<CategoryDto>(TestContext.CancellationToken))!.Version!.Value;

        // Second writer still holds the pre-first-write version.
        using var second = await client.PutWithIfMatchAsync(
            $"/api/v1/categories/{created.Id}",
            new DefaultRequest<CategoryDto> { Item = created with { Name = "Second" } },
            ConcurrencyHttpExtensions.IfMatch(created.Version!.Value),
            TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.PreconditionFailed, second.StatusCode);
        Assert.AreEqual(currentVersion.ToString(), second.ETagValue(),
            "A 412 must return the current version so the caller can retry without an extra GET.");
    }

    /// <summary>Verifies the wildcard override applies the write regardless of the stored version.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_WildcardIfMatch_When_Put_Then_Applies(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var created = await CreateCategoryAsync(client, $"Wildcard-{Guid.NewGuid():N}");

        using var bump = await client.PutWithIfMatchAsync(
            $"/api/v1/categories/{created.Id}",
            new DefaultRequest<CategoryDto> { Item = created with { Name = "Bumped" } },
            ConcurrencyHttpExtensions.IfMatch(created.Version!.Value),
            TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, bump.StatusCode);

        // Deliberately stale caller, but with the trusted-automation override.
        using var response = await client.PutWithIfMatchAsync(
            $"/api/v1/categories/{created.Id}",
            new DefaultRequest<CategoryDto> { Item = created with { Name = "Forced" } },
            "*",
            TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var forced = await response.ItemAsync<CategoryDto>(TestContext.CancellationToken);
        Assert.AreEqual("Forced", forced!.Name);
    }

    /// <summary>Verifies a stale delete is refused with 412 and the row survives.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_StaleIfMatch_When_Delete_Then_Returns412AndRowSurvives(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var created = await CreateCategoryAsync(client, $"StaleDel-{Guid.NewGuid():N}");

        using var bump = await client.PutWithIfMatchAsync(
            $"/api/v1/categories/{created.Id}",
            new DefaultRequest<CategoryDto> { Item = created with { Name = "Bumped" } },
            ConcurrencyHttpExtensions.IfMatch(created.Version!.Value),
            TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, bump.StatusCode);

        using var response = await client.DeleteWithIfMatchAsync(
            $"/api/v1/categories/{created.Id}",
            ConcurrencyHttpExtensions.IfMatch(created.Version!.Value),
            TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.PreconditionFailed, response.StatusCode);

        using var get = await client.GetAsync($"/api/v1/categories/{created.Id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, get.StatusCode);
    }

    /// <summary>Verifies PATCH carries the same precondition as PUT in both styles (CQRS PATCH parity).</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_TaskItem_When_PatchWithIfMatch_Then_AppliesAndRejectsStale(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);

        using var createResponse = await client.PostAsJsonAsync("/api/v1/task-items",
            new DefaultRequest<TaskItemDto> { Item = new TaskItemDto { Title = $"Patch-{Guid.NewGuid():N}" } },
            cancellationToken: TestContext.CancellationToken);
        var created = (await createResponse.ItemAsync<TaskItemDto>(TestContext.CancellationToken))!;

        using var patched = await client.PatchWithIfMatchAsync(
            $"/api/v1/task-items/{created.Id}",
            new DefaultRequest<TaskItemPatchDto> { Item = new TaskItemPatchDto { Title = "Patched" } },
            ConcurrencyHttpExtensions.IfMatch(created.Version!.Value),
            TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, patched.StatusCode);
        Assert.AreEqual("Patched", (await patched.ItemAsync<TaskItemDto>(TestContext.CancellationToken))!.Title);

        using var stale = await client.PatchWithIfMatchAsync(
            $"/api/v1/task-items/{created.Id}",
            new DefaultRequest<TaskItemPatchDto> { Item = new TaskItemPatchDto { Title = "Stale" } },
            ConcurrencyHttpExtensions.IfMatch(created.Version!.Value),
            TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    /// <summary>Verifies a child mutation moves the root ETag (D-031: one concurrency currency per aggregate).</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ChildAdded_When_RootRead_Then_RootETagChanged(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);

        using var createResponse = await client.PostAsJsonAsync("/api/v1/task-items",
            new DefaultRequest<TaskItemDto> { Item = new TaskItemDto { Title = $"ChildBump-{Guid.NewGuid():N}" } },
            cancellationToken: TestContext.CancellationToken);
        var created = (await createResponse.ItemAsync<TaskItemDto>(TestContext.CancellationToken))!;
        var rootVersionBefore = created.Version!.Value;

        using var addComment = await client.PostAsJsonAsync(
            $"/api/v1/task-items/{created.Id}/comments",
            new DefaultRequest<CommentDto> { Item = new CommentDto { Body = "Bumps the root", TaskItemId = created.Id!.Value } },
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, addComment.StatusCode);

        using var get = await client.GetAsync($"/api/v1/task-items/{created.Id}", TestContext.CancellationToken);
        var rootVersionAfter = (await get.ItemAsync<TaskItemDto>(TestContext.CancellationToken))!.Version!.Value;

        Assert.IsGreaterThan(rootVersionBefore, rootVersionAfter,
            "A child mutation must move the root aggregate version, or the root ETag lies about the aggregate.");
        Assert.AreEqual(rootVersionAfter.ToString(), addComment.ETagValue(),
            "The child add response must return the new root version as its ETag.");

        // A caller holding the pre-child root version is now stale for a root write.
        using var stale = await client.PutWithIfMatchAsync(
            $"/api/v1/task-items/{created.Id}",
            new DefaultRequest<TaskItemDto> { Item = created with { Title = "Stale root write" } },
            ConcurrencyHttpExtensions.IfMatch(rootVersionBefore),
            TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.PreconditionFailed, stale.StatusCode);
    }

    /// <summary>Verifies a caller-supplied non-v7 id is refused with 400 (GR-17).</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_NonUuidV7CallerId_When_Create_Then_Returns400(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);

        using var response = await client.PostAsJsonAsync("/api/v1/categories",
            new DefaultRequest<CategoryDto>
            {
                // Guid.NewGuid() is a v4: time-unordered, and the reason the contract requires v7.
                Item = new CategoryDto { Id = Guid.NewGuid(), Name = $"V4-{Guid.NewGuid():N}", IsActive = true }
            },
            cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Verifies an equivalent replay returns 200 with the stored entity instead of creating a second row.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_EquivalentReplay_When_Create_Then_Returns200WithExisting(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);

        var id = Guid.CreateVersion7();
        var payload = new CategoryDto { Id = id, Name = $"Replay-{Guid.NewGuid():N}", IsActive = true, SortOrder = 3 };

        using var first = await client.PostAsJsonAsync("/api/v1/categories",
            new DefaultRequest<CategoryDto> { Item = payload }, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);
        var createdVersion = (await first.ItemAsync<CategoryDto>(TestContext.CancellationToken))!.Version;

        using var replay = await client.PostAsJsonAsync("/api/v1/categories",
            new DefaultRequest<CategoryDto> { Item = payload }, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, replay.StatusCode, "A replay creates nothing, so it is 200, not 201.");
        var replayed = await replay.ItemAsync<CategoryDto>(TestContext.CancellationToken);
        Assert.AreEqual(id, replayed!.Id);
        Assert.AreEqual(createdVersion, replayed.Version, "A replay must not bump the stored version.");
    }

    /// <summary>Verifies a divergent payload on an existing caller id is a 409, not a silent overwrite.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_DivergentReplay_When_Create_Then_Returns409(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);

        var id = Guid.CreateVersion7();
        var name = $"Conflict-{Guid.NewGuid():N}";

        using var first = await client.PostAsJsonAsync("/api/v1/categories",
            new DefaultRequest<CategoryDto> { Item = new CategoryDto { Id = id, Name = name, IsActive = true } },
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, first.StatusCode);

        using var divergent = await client.PostAsJsonAsync("/api/v1/categories",
            new DefaultRequest<CategoryDto> { Item = new CategoryDto { Id = id, Name = name + "-different", IsActive = true } },
            cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Conflict, divergent.StatusCode);
    }

    public TestContext TestContext { get; set; } = null!;
}
