using EF.Common.Contracts;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TaskFlow.Application.Models;

namespace Test.Endpoints;

/// <summary>
/// HTTP contract tests for <c>/api/v1/categories</c> CRUD plus search and a full create->read->update->delete
/// round-trip.
/// Endpoint tier (WebApplicationFactory + EF InMemory via <c>CustomApiFactory</c>): verifies status codes,
/// envelope shape, and search projection over the Categories endpoint without spinning a real SQL container.
/// </summary>
[TestClass]
public class CategoryEndpointTests
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

    /// <summary>Verifies that given valid payload, when post category, then returns 201.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ValidPayload_When_PostCategory_Then_Returns201(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var dto = new CategoryDto { Name = "Test Category", SortOrder = 1, IsActive = true };

        var response = await client.PostAsJsonAsync("/api/v1/categories", new DefaultRequest<CategoryDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        var created = (await response.Content.ReadFromJsonAsync<DefaultResponse<CategoryDto>>(TestContext.CancellationToken))!.Item;
        Assert.IsNotNull(created);
        Assert.AreEqual("Test Category", created.Name);
        Assert.IsNotNull(created.Id);
    }

    /// <summary>Verifies that given existing category, when get by ID, then returns 200.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ExistingCategory_When_GetById_Then_Returns200(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var dto = new CategoryDto { Name = "GetTest Category", SortOrder = 1, IsActive = true };
        var createResponse = await client.PostAsJsonAsync("/api/v1/categories", new DefaultRequest<CategoryDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<DefaultResponse<CategoryDto>>(TestContext.CancellationToken))!.Item;

        var response = await client.GetAsync($"/api/v1/categories/{created!.Id}", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var result = (await response.Content.ReadFromJsonAsync<DefaultResponse<CategoryDto>>(TestContext.CancellationToken))!.Item;
        Assert.IsNotNull(result);
        Assert.AreEqual("GetTest Category", result.Name);
    }

    /// <summary>Verifies that given non existent ID, when get category, then returns 404.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_NonExistentId_When_GetCategory_Then_Returns404(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);

        var response = await client.GetAsync($"/api/v1/categories/{Guid.NewGuid()}", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>Verifies that given existing category, when put update, then returns 200.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ExistingCategory_When_PutUpdate_Then_Returns200(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var dto = new CategoryDto { Name = "Before", SortOrder = 1, IsActive = true };
        var createResponse = await client.PostAsJsonAsync("/api/v1/categories", new DefaultRequest<CategoryDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<DefaultResponse<CategoryDto>>(TestContext.CancellationToken))!.Item;

        var updateDto = new CategoryDto
        {
            Id = created!.Id,
            Name = "After",
            SortOrder = 2,
            IsActive = true
        };
        var response = await client.PutWithIfMatchAsync($"/api/v1/categories/{created.Id}", new DefaultRequest<CategoryDto> { Item = updateDto }, ConcurrencyHttpExtensions.IfMatch(created.Version!.Value), TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var updated = (await response.Content.ReadFromJsonAsync<DefaultResponse<CategoryDto>>(TestContext.CancellationToken))!.Item;
        Assert.AreEqual("After", updated!.Name);
    }

    /// <summary>Verifies that given existing category, when delete, then returns 204.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ExistingCategory_When_Delete_Then_Returns204(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);
        var dto = new CategoryDto { Name = "ToDelete Category", SortOrder = 1, IsActive = true };
        var createResponse = await client.PostAsJsonAsync("/api/v1/categories", new DefaultRequest<CategoryDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        var created = (await createResponse.Content.ReadFromJsonAsync<DefaultResponse<CategoryDto>>(TestContext.CancellationToken))!.Item;

        var response = await client.DeleteWithIfMatchAsync($"/api/v1/categories/{created!.Id}", ConcurrencyHttpExtensions.IfMatch(created.Version!.Value), TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NoContent, response.StatusCode);

        var getResponse = await client.GetAsync($"/api/v1/categories/{created.Id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, getResponse.StatusCode);
    }

    /// <summary>Verifies that given existing categories, when search, then returns filtered page.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ExistingCategories_When_Search_Then_ReturnsFilteredPage(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = CreateClient(style);

        await client.PostAsJsonAsync("/api/v1/categories",
            new DefaultRequest<CategoryDto> { Item = new CategoryDto { Name = "SearchMe Cat", SortOrder = 1, IsActive = true } }, cancellationToken: TestContext.CancellationToken);
        await client.PostAsJsonAsync("/api/v1/categories",
            new DefaultRequest<CategoryDto> { Item = new CategoryDto { Name = "Other Cat", SortOrder = 2, IsActive = true } }, cancellationToken: TestContext.CancellationToken);

        var searchRequest = new SearchRequest<CategorySearchFilter>
        {
            PageIndex = 0,
            PageSize = 10,
            Filter = new CategorySearchFilter { SearchTerm = "SearchMe" }
        };

        var response = await client.PostAsJsonAsync("/api/v1/categories/search", searchRequest, cancellationToken: TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(TestContext.CancellationToken), cancellationToken: TestContext.CancellationToken);
        var root = doc.RootElement;
        Assert.IsGreaterThanOrEqualTo(root.GetProperty("total").GetInt32(), 1);
        var data = root.GetProperty("data");
        Assert.Contains(e => e.GetProperty("name").GetString()!.Contains("SearchMe"), data.EnumerateArray());
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
        var dto = new CategoryDto { Name = "CrudCycle Cat", SortOrder = 1, IsActive = true };
        var createResponse = await client.PostAsJsonAsync("/api/v1/categories", new DefaultRequest<CategoryDto> { Item = dto }, cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, createResponse.StatusCode);
        var created = (await createResponse.Content.ReadFromJsonAsync<DefaultResponse<CategoryDto>>(TestContext.CancellationToken))!.Item;

        // Read
        var getResponse = await client.GetAsync($"/api/v1/categories/{created!.Id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, getResponse.StatusCode);

        // Update
        var updateDto = new CategoryDto
        {
            Id = created.Id,
            Name = "CrudCycle Cat Updated",
            SortOrder = 5,
            IsActive = false
        };
        var updateResponse = await client.PutWithIfMatchAsync($"/api/v1/categories/{created.Id}", new DefaultRequest<CategoryDto> { Item = updateDto }, ConcurrencyHttpExtensions.IfMatch(created.Version!.Value), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.OK, updateResponse.StatusCode);

        // Delete
        var updatedItem = (await updateResponse.Content.ReadFromJsonAsync<DefaultResponse<CategoryDto>>(TestContext.CancellationToken))!.Item;
        var deleteResponse = await client.DeleteWithIfMatchAsync($"/api/v1/categories/{created.Id}", ConcurrencyHttpExtensions.IfMatch(updatedItem!.Version!.Value), TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NoContent, deleteResponse.StatusCode);

        // Verify deleted
        var verifyResponse = await client.GetAsync($"/api/v1/categories/{created.Id}", TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.NotFound, verifyResponse.StatusCode);
    }

    public TestContext TestContext { get; set; } = null!;
}
