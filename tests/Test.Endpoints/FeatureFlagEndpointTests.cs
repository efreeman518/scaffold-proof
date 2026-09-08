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
