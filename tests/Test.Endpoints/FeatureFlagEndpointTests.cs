using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Configuration;
using TaskFlow.Application.Models;

namespace Test.Endpoints;

/// <summary>
/// D-042: HTTP-level coverage for the feature-flag gate. TaskViews and Export answer 404 while their
/// flag is off (FeatureGateEndpointFilter), and pass through to the normal handler once it is on. Also
/// proves the local appsettings fallback: CustomApiFactory never sets AppConfig:Endpoint, so every
/// other passing test in this suite is already evidence the host boots without Azure App Configuration -
/// the last test here names that expectation explicitly.
/// </summary>
[TestClass]
public sealed class FeatureFlagEndpointTests
{
    /// <summary>Seeds one task and returns its title.</summary>
    private async Task<string> SeedTaskAsync(HttpClient client)
    {
        var title = $"FeatureFlag-{Guid.NewGuid():N}";
        using var response = await client.PostAsJsonAsync("/api/v1/task-items",
            new DefaultRequest<TaskItemDto> { Item = new TaskItemDto { Title = title } },
            cancellationToken: TestContext.CancellationToken);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode);
        return title;
    }

    [TestCategory("Endpoint")]
    [TestMethod]
    public async Task Given_ExportFlagOff_When_Export_Then_NotFound()
    {
        using var factory = new CustomApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FeatureManagement:Export"] = "false"
                })));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/task-items/export", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestCategory("Endpoint")]
    [TestMethod]
    public async Task Given_ExportFlagOn_When_Export_Then_StreamsSeededTask()
    {
        using var factory = new CustomApiFactory();
        using var client = factory.CreateClient();
        var title = await SeedTaskAsync(client);

        using var response = await client.GetAsync("/api/v1/task-items/export", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        StringAssert.Contains(body, title);
    }

    [TestCategory("Endpoint")]
    [TestMethod]
    public async Task Given_TaskViewsFlagOff_When_ListTaskViews_Then_NotFound()
    {
        using var factory = new CustomApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // Relational so the assertion is not accidentally satisfied by the Cosmos default
                    // arm short-circuiting for an unconfigured connection string.
                    ["ReadModel:Provider"] = "Relational",
                    ["FeatureManagement:TaskViews"] = "false"
                })));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/v1/task-views?tenantId={Guid.NewGuid()}", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestCategory("Endpoint")]
    [TestMethod]
    public async Task Given_TaskViewsFlagOn_When_ListTaskViews_Then_Ok()
    {
        using var factory = new CustomApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ReadModel:Provider"] = "Relational"
                })));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            $"/api/v1/task-views?tenantId={Guid.NewGuid()}", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// GR-19/GR-20: SemanticSearch gates one search mode, not the whole route. With the flag off a Semantic
    /// request is 404 - the surface looks absent rather than forbidden.
    /// </summary>
    [TestCategory("Endpoint")]
    [TestMethod]
    public async Task Given_SemanticSearchFlagOff_When_SemanticSearch_Then_NotFound()
    {
        using var factory = new CustomApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FeatureManagement:SemanticSearch"] = "false"
                })));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/v1/search/tasks?query=release&mode=Semantic&maxResults=10", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// The gate is per-mode: keyword search answers normally while SemanticSearch is off, so turning the flag
    /// off cannot take the whole route down with it.
    /// </summary>
    [TestCategory("Endpoint")]
    [TestMethod]
    public async Task Given_SemanticSearchFlagOff_When_KeywordSearch_Then_StillAnswers()
    {
        using var factory = new CustomApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["FeatureManagement:SemanticSearch"] = "false"
                })));
        using var client = factory.CreateClient();

        using var response = await client.GetAsync(
            "/api/v1/search/tasks?query=release&mode=Keyword&maxResults=10", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Flag on, Search:Provider=Sql: the request passes the gate and is answered by the prefix arm, which is
    /// what the InMemory harness can run. The PgVector arm itself is covered in Test.Integration.
    /// </summary>
    [TestCategory("Endpoint")]
    [TestMethod]
    public async Task Given_SemanticSearchFlagOn_And_SqlSearchProvider_When_Search_Then_ReturnsPrefixResults()
    {
        using var factory = new CustomApiFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Search:Provider"] = "Sql",
                    ["FeatureManagement:SemanticSearch"] = "true"
                })));
        using var client = factory.CreateClient();
        var title = await SeedTaskAsync(client);

        using var response = await client.GetAsync(
            $"/api/v1/search/tasks?query={title}&mode=Semantic&maxResults=10", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync(TestContext.CancellationToken);
        StringAssert.Contains(body, title);
    }

    /// <summary>
    /// Boot test: CustomApiFactory sets no AppConfig:Endpoint/ConnectionStrings:AppConfig, so
    /// AddTaskFlowAppConfiguration is a no-op and every flag comes from the FeatureManagement section in
    /// appsettings.json - proven here by a route gated behind one of them still answering normally.
    /// </summary>
    [TestCategory("Endpoint")]
    [TestMethod]
    public async Task Given_NoAppConfigEndpoint_When_HostBoots_Then_AppsettingsFeatureFlagsApply()
    {
        using var factory = new CustomApiFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/api/v1/task-items/export", TestContext.CancellationToken);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    public TestContext TestContext { get; set; } = null!;
}
