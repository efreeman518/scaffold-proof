using System.Net;
using System.Net.Http.Json;
using TaskFlow.Application.Models;

namespace Test.Endpoints;

/// <summary>
/// HTTP contract tests for <c>/api/v1/tags</c> CRUD: status codes, response envelopes, and 404 on
/// non-existent ids.
/// Endpoint tier (WebApplicationFactory + EF InMemory via <c>CustomApiFactory</c>): exercises routing,
/// model binding, ProblemDetails shape and middleware against the real ASP.NET Core pipeline.
/// A pure-unit tier would miss the wire format and HTTP semantics; SQL tier is unnecessary because no
/// concurrency or projection-plan behavior is asserted.
/// </summary>
[TestClass]
public class TagEndpointTests
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

    /// <summary>Verifies that given valid payload, when post tag, then returns 201.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ValidPayload_When_PostTag_Then_Returns201(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var dto = new TagDto { Name = "Urgent", Color = "#FF0000" };

        var response = await client.PostAsJsonAsync("/api/v1/tags", new DefaultRequest<TagDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<DefaultResponse<TagDto>>(TestContext.CancellationToken))!.Item;
        Assert.IsNotNull(created);
        Assert.AreEqual("Urgent", created.Name);
        Assert.IsNotNull(created.Id);
    }

    /// <summary>Verifies that given existing tag, when get by ID, then returns 200.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ExistingTag_When_GetById_Then_Returns200(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var dto = new TagDto { Name = "GetTag", Color = "#00FF00" };
        var createResponse = await client.PostAsJsonAsync("/api/v1/tags", new DefaultRequest<TagDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<DefaultResponse<TagDto>>(TestContext.CancellationToken))!.Item;

        var response = await client.GetAsync($"/api/v1/tags/{created!.Id}", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<DefaultResponse<TagDto>>(TestContext.CancellationToken))!.Item;
        Assert.IsNotNull(result);
        Assert.AreEqual("GetTag", result.Name);
    }

    /// <summary>Verifies that given non existent ID, when get tag, then returns 404.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_NonExistentId_When_GetTag_Then_Returns404(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);

        var response = await client.GetAsync($"/api/v1/tags/{Guid.NewGuid()}", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Verifies that given existing tag, when put update, then returns 200.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ExistingTag_When_PutUpdate_Then_Returns200(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var dto = new TagDto { Name = "BeforeTag", Color = "#111111" };
        var createResponse = await client.PostAsJsonAsync("/api/v1/tags", new DefaultRequest<TagDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<DefaultResponse<TagDto>>(TestContext.CancellationToken))!.Item;

        var updateDto = new TagDto { Id = created!.Id, Name = "AfterTag", Color = "#222222" };
        var response = await client.PutWithIfMatchAsync($"/api/v1/tags/{created.Id}", new DefaultRequest<TagDto> { Item = updateDto }, ConcurrencyHttpExtensions.IfMatch(created.Version!.Value), TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<DefaultResponse<TagDto>>(TestContext.CancellationToken))!.Item;
        Assert.AreEqual("AfterTag", updated!.Name);
    }

    /// <summary>Verifies that given existing tag, when delete, then returns 204.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ExistingTag_When_Delete_Then_Returns204(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var dto = new TagDto { Name = "ToDeleteTag", Color = "#333333" };
        var createResponse = await client.PostAsJsonAsync("/api/v1/tags", new DefaultRequest<TagDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<DefaultResponse<TagDto>>(TestContext.CancellationToken))!.Item;

        var response = await client.DeleteWithIfMatchAsync($"/api/v1/tags/{created!.Id}", ConcurrencyHttpExtensions.IfMatch(created.Version!.Value), TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await client.GetAsync($"/api/v1/tags/{created.Id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    public TestContext TestContext { get; set; } = null!;
}
