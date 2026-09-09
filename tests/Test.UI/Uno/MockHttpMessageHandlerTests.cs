using TaskFlow.Uno.Core.Business.Services;
using TaskFlow.Uno.Core.Client;

namespace Test.UI.Uno;

/// <summary>
/// Validates the <c>MockHttpMessageHandler</c> used by the Uno design-mode client: routes return the
/// expected mock payloads (TaskItems cursor search/summary/metadata, Categories, Tags), DELETE returns
/// 204, and unknown routes return 404.
/// Pure-unit tier: the handler is run inside an <c>HttpClient</c> with no real network - verifies the
/// mock surface alone, not API behavior (which Test.Endpoints and Test.E2E cover).
/// </summary>
[TestClass]
[TestCategory("UI")]
public class MockHttpMessageHandlerTests
{
    private MockHttpMessageHandler _handler = null!;
    private HttpClient _httpClient = null!;

    /// <summary>Prepares per-test fixtures so each test starts from a predictable state.</summary>
    [TestInitialize]
    public void Setup()
    {
        _handler = new MockHttpMessageHandler();
        _httpClient = new HttpClient(_handler) { BaseAddress = new Uri("https://localhost:7200") };
    }

    /// <summary>Verifies teardown behavior and protects the expected test contract.</summary>
    [TestCleanup]
    public void Teardown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    /// <summary>Verifies the cursor search route returns mock data with cursor paging metadata.</summary>
    [TestMethod]
    public async Task SearchTaskItemsCursor_ReturnsMockData()
    {
        var response = await _httpClient.PostAsJsonAsync("/api/v1/task-items/search",
            new TaskItemCursorSearchRequest { PageSize = 5 }, cancellationToken: TestContext.CancellationToken);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<CursorPage<TaskItemDto>>(TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.IsNotEmpty(result.Data!);
        Assert.AreEqual("Build dashboard UI", result.Data![0].Title);
        Assert.IsTrue(result.HasMore);
        Assert.IsNotNull(result.NextCursor);
    }

    /// <summary>Verifies the tenant summary route returns per-status counts.</summary>
    [TestMethod]
    public async Task GetTaskItemsSummary_ReturnsMockData()
    {
        var response = await _httpClient.GetAsync("/api/v1/task-items/summary", TestContext.CancellationToken);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TaskItemSummaryDto>(TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.IsTrue(result.Total > 0);
        Assert.IsNotEmpty(result.ByStatus!);
    }

    /// <summary>Verifies the task-metadata route returns the full category and tag lists.</summary>
    [TestMethod]
    public async Task GetTaskMetadata_ReturnsMockData()
    {
        var response = await _httpClient.GetAsync("/api/v1/task-metadata", TestContext.CancellationToken);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<TaskMetadataDto>(TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.IsNotEmpty(result.Categories!);
        Assert.IsNotEmpty(result.Tags!);
    }

    /// <summary>Verifies search categories returns mock data behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task SearchCategories_ReturnsMockData()
    {
        var response = await _httpClient.PostAsJsonAsync("/api/v1/categories/search",
            new SearchRequest<CategorySearchFilter> { PageIndex = 1, PageSize = 100 }, cancellationToken: TestContext.CancellationToken);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResponse<CategoryDto>>(TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.IsNotEmpty(result.Data!);
        Assert.AreEqual("Development", result.Data![0].Name);
    }

    /// <summary>Verifies search tags returns mock data behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task SearchTags_ReturnsMockData()
    {
        var response = await _httpClient.PostAsJsonAsync("/api/v1/tags/search",
            new SearchRequest<TagSearchFilter> { PageIndex = 1, PageSize = 100 }, cancellationToken: TestContext.CancellationToken);

        response.EnsureSuccessStatusCode();
        var result = await response.Content.ReadFromJsonAsync<PagedResponse<TagDto>>(TestContext.CancellationToken);

        Assert.IsNotNull(result);
        Assert.IsNotEmpty(result.Data!);
        // Mock SearchTags orders alphabetically - "backend" sorts before "frontend".
        Assert.AreEqual("backend", result.Data![0].Name);
    }

    /// <summary>Verifies delete task item returns no content behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task DeleteTaskItem_ReturnsNoContent()
    {
        var response = await _httpClient.DeleteAsync($"/api/v1/task-items/{Guid.NewGuid()}", TestContext.CancellationToken);

        Assert.AreEqual(System.Net.HttpStatusCode.NoContent, response.StatusCode);
    }

    /// <summary>Verifies unknown route returns not found behavior and protects the expected test contract.</summary>
    [TestMethod]
    public async Task UnknownRoute_ReturnsNotFound()
    {
        var response = await _httpClient.GetAsync("/api/unknown-endpoint", TestContext.CancellationToken);

        Assert.AreEqual(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    public TestContext TestContext { get; set; } = null!;
}
