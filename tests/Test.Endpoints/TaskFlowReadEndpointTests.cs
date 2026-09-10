using EF.Common.Contracts;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Paging;

namespace Test.Endpoints;

/// <summary>
/// Contract tests for the style-agnostic read endpoints: <c>/task-items/summary</c>,
/// <c>/task-metadata</c>, and the NDJSON <c>/task-items/export</c> stream. They are mapped once for
/// both styles, so the matrix here proves the single registration really does serve both.
/// </summary>
[TestClass]
public class TaskFlowReadEndpointTests
{
    private static EndpointStyleFixture _fixture = null!;

    /// <summary>Initializes shared test fixtures before the class-level test run begins.</summary>
    [ClassInitialize]
    public static void ClassInit(TestContext _) => _fixture = new EndpointStyleFixture();

    /// <summary>Disposes shared test fixtures after the class-level test run finishes.</summary>
    [ClassCleanup]
    public static void ClassCleanup() => _fixture?.Dispose();

    /// <summary>Seeds one task and returns its title.</summary>
    private async Task<string> SeedTaskAsync(HttpClient client)
    {
        var title = $"Read-{Guid.NewGuid():N}";
        using var response = await client.PostAsJsonAsync("/api/v1/task-items",
            new DefaultRequest<TaskItemDto> { Item = new TaskItemDto { Title = title } },
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return title;
    }

    /// <summary>Verifies the summary reports counts by status plus overdue and total.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_SeededTasks_When_GetSummary_Then_ReturnsCounts(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        await SeedTaskAsync(client);

        using var response = await client.GetAsync("/api/v1/task-items/summary", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.IsGreaterThanOrEqualTo(1, doc.RootElement.GetProperty("total").GetInt32());
        Assert.IsGreaterThanOrEqualTo(1, doc.RootElement.GetProperty("byStatus").GetArrayLength());
        Assert.IsTrue(doc.RootElement.TryGetProperty("overdue", out _));
        Assert.IsTrue(doc.RootElement.TryGetProperty("generatedAtUtc", out _));
    }

    /// <summary>Verifies metadata returns full category and tag lists, which replaced oversized searches.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_SeededMetadata_When_GetTaskMetadata_Then_ReturnsCategoriesAndTags(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);

        var categoryName = $"MetaCat-{Guid.NewGuid():N}";
        using var category = await client.PostAsJsonAsync("/api/v1/categories",
            new DefaultRequest<CategoryDto> { Item = new CategoryDto { Name = categoryName, IsActive = true } },
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, category.StatusCode);

        var tagName = $"metatag-{Guid.NewGuid():N}";
        using var tag = await client.PostAsJsonAsync("/api/v1/tags",
            new DefaultRequest<TagDto> { Item = new TagDto { Name = tagName } },
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, tag.StatusCode);

        using var response = await client.GetAsync("/api/v1/task-metadata", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.CancellationToken));
        Assert.Contains(e => e.GetProperty("name").GetString() == categoryName,
            doc.RootElement.GetProperty("categories").EnumerateArray());
        Assert.Contains(e => e.GetProperty("name").GetString() == tagName,
            doc.RootElement.GetProperty("tags").EnumerateArray());
    }

    /// <summary>Verifies the export streams one JSON object per line and covers rows past the first batch.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_SeededTasks_When_Export_Then_StreamsNdJsonAcrossBatches(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);

        var titles = new List<string>();
        for (var i = 0; i < 5; i++) titles.Add(await SeedTaskAsync(client));

        // A batch size below the row count forces the endpoint's resume loop to run more than once.
        using var response = await client.GetAsync("/api/v1/task-items/export?batchSize=2", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("application/x-ndjson", response.Content.Headers.ContentType?.MediaType);

        var body = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        var lines = body.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.IsGreaterThanOrEqualTo(5, lines.Length);

        var exported = lines.Select(line => JsonDocument.Parse(line).RootElement.GetProperty("title").GetString()).ToHashSet();
        foreach (var title in titles) Assert.Contains(title, exported);
    }

    /// <summary>Verifies an out-of-range export batch size is refused for the same reason a search page size is.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_BatchSizeAboveMax_When_Export_Then_Returns400(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);

        using var response = await client.GetAsync(
            $"/api/v1/task-items/export?batchSize={PageSizeLimits.Max + 1}", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
    }

    /// <summary>Verifies a client that abandons the export mid-stream cancels it instead of hanging.</summary>
    [TestCategory("Endpoint")]
    [DataRow(EndpointStyles.Service)]
    [DataRow(EndpointStyles.Cqrs)]
    [TestMethod]
    public async Task Given_ClientCancels_When_Export_Then_StreamStops(string style)
    {
        EndpointStyles.SkipWhenStyleForced();
        using var client = _fixture.CreateClient(style);
        for (var i = 0; i < 5; i++) await SeedTaskAsync(client);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        using var response = await client.GetAsync(
            "/api/v1/task-items/export?batchSize=1", HttpCompletionOption.ResponseHeadersRead, cts.Token);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        await cts.CancelAsync();

        await Assert.ThrowsExactlyAsync<TaskCanceledException>(
            async () => await response.Content.ReadAsStringAsync(cts.Token));
    }

    public TestContext TestContext { get; set; } = null!;
}
