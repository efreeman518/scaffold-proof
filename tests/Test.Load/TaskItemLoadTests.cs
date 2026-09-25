using EF.Common.Contracts;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TaskFlow.Application.Models;
using TaskFlow.Domain.Shared.Enums;
using Test.Support;

[assembly: DoNotParallelize]

namespace Test.Load;

/// <summary>
/// LoadRunner scenarios against the TaskItem search and CRUD endpoints, asserting error-rate and P95/P99
/// latency baselines with the in-house, open-model <see cref="LoadRunner"/> (GR-04: no commercial-license
/// load package).
/// Load tier: both methods are <c>[Ignore]</c>'d for CI and require a running <c>taskflowapi</c> endpoint.
/// Faster tiers (Endpoint/E2E) cannot reproduce the concurrent-load behavior these baselines guard.
/// Manual run:
/// 1. Remove or comment the <c>[Ignore("Run manually - requires API host running")]</c>
///    attribute on the load test method to run.
/// 2. From repo root, start the Aspire host:
///    <c>dotnet run --project src\Host\Aspire\AppHost\AppHost.csproj</c>.
/// 3. Use the <c>taskflowapi</c> HTTP endpoint from Aspire. Default is <c>http://localhost:5188</c>.
///    If Aspire assigns another port, set <c>TASKFLOW_LOAD_BASE_URL</c> before running tests.
/// 4. From the same repo root run:
///    <c>$env:TASKFLOW_LOAD_BASE_URL="http://localhost:5188"; dotnet test tests\Test.Load\Test.Load.csproj --filter TestCategory=Load</c>.
/// Default simulations stay below the API's 100 request/minute tenant rate limit. Raise
/// <c>RateLimiting:PerTenant:PermitLimit</c> before using higher injection profiles.
/// </summary>
[TestClass]
[TestCategory("Load")]
public class TaskItemLoadTests
{
    private const string DefaultBaseUrl = "http://localhost:5188";
    private static readonly string BaseUrl = Environment.GetEnvironmentVariable("TASKFLOW_LOAD_BASE_URL") ?? DefaultBaseUrl;
    private const string TaskItemsPath = "/api/v1/task-items";
    private const string TaskItemsSearchPath = "/api/v1/task-items/search";
    private static readonly JsonSerializerOptions JsonOptions = JsonTestOptions.Default;

    // Reused across every request in this class - one connection pool, not one client per call.
    private static readonly HttpClient HttpClient = new() { BaseAddress = new Uri(BaseUrl) };

    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    /// <summary>Verifies that given task item search endpoint, when load applied, then meets performance baseline.</summary>
    [TestMethod]
    [Ignore("Run manually - requires API host running")]
    public async Task Given_TaskItemSearchEndpoint_When_LoadApplied_Then_MeetsPerformanceBaseline()
    {
        var searchRequest = new SearchRequest<TaskItemSearchFilter>
        {
            PageIndex = 0,
            PageSize = 20,
            Filter = new TaskItemSearchFilter()
        };

        // Starts one new search invocation per second for 30 seconds - unchanged from the prior configuration.
        var result = await LoadRunner.RunAsync(
            async ct =>
            {
                using var response = await HttpClient.PostAsJsonAsync(TaskItemsSearchPath, searchRequest, JsonOptions, ct);
                return response.IsSuccessStatusCode;
            },
            ratePerSecond: 1, duration: TimeSpan.FromSeconds(30), maxInFlight: 20, TestContext.CancellationToken);

        TestContext.WriteLine($"{result}");
        Assert.IsLessThanOrEqualTo(0.05, result.ErrorRate, $"Error rate {result.ErrorRate:P2} exceeds budget.");
        Assert.IsLessThan(TimeSpan.FromMilliseconds(2000), result.P99, $"p99 {result.P99} exceeds budget.");
        Assert.IsLessThan(TimeSpan.FromMilliseconds(1000), result.P95, $"p95 {result.P95} exceeds budget.");
    }

    /// <summary>Verifies that given task item CRUD workflow, when load applied, then meets throughput baseline.</summary>
    [TestMethod]
    [Ignore("Run manually - requires API host running")]
    public async Task Given_TaskItemCrudWorkflow_When_LoadApplied_Then_MeetsThroughputBaseline()
    {
        // The prior ramp (30s) and steady (60s) phases both injected at rate 1; the in-house runner has no
        // ramping concept, so they collapse into one flat rate 1 run over the combined 90s duration.
        var result = await LoadRunner.RunAsync(
            ct => RunCrudWorkflowAsync(HttpClient, ct),
            ratePerSecond: 1, duration: TimeSpan.FromSeconds(90), maxInFlight: 10, TestContext.CancellationToken);

        TestContext.WriteLine($"{result}");
        Assert.IsLessThanOrEqualTo(0.05, result.ErrorRate, $"CRUD workflow error rate {result.ErrorRate:P2} exceeds budget.");
        Assert.IsLessThan(TimeSpan.FromMilliseconds(2000), result.P99, $"p99 {result.P99} exceeds budget.");
        Assert.IsLessThan(TimeSpan.FromMilliseconds(1000), result.P95, $"p95 {result.P95} exceeds budget.");
    }

    /// <summary>Runs one search-create-get-update-delete-verify cycle; false on any unexpected status code.</summary>
    private static async Task<bool> RunCrudWorkflowAsync(HttpClient httpClient, CancellationToken ct)
    {
        using var searchResponse = await httpClient.PostAsJsonAsync(
            TaskItemsSearchPath,
            new SearchRequest<TaskItemSearchFilter> { PageIndex = 0, PageSize = 10, Filter = new TaskItemSearchFilter() },
            JsonOptions, ct);
        if (!searchResponse.IsSuccessStatusCode) return false;

        var createRequest = new DefaultRequest<TaskItemDto>
        {
            Item = new TaskItemDto
            {
                Title = $"Load test task {Guid.NewGuid():N}",
                Description = "Generated by load test",
                Priority = Priority.Medium,
                Status = TaskItemStatus.Open
            }
        };
        using var createResponse = await httpClient.PostAsJsonAsync(TaskItemsPath, createRequest, JsonOptions, ct);
        if (createResponse.StatusCode != HttpStatusCode.Created) return false;
        var created = await createResponse.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(JsonOptions, ct);
        var id = created?.Item?.Id ?? throw new InvalidOperationException("Create did not return a TaskItem id.");

        using var getResponse = await httpClient.GetAsync($"{TaskItemsPath}/{id}", ct);
        if (getResponse.StatusCode != HttpStatusCode.OK) return false;
        var fetched = await getResponse.Content.ReadFromJsonAsync<DefaultResponse<TaskItemDto>>(JsonOptions, ct);
        var current = fetched?.Item ?? throw new InvalidOperationException("Get did not return a TaskItem.");

        var updateRequest = new DefaultRequest<TaskItemDto>
        {
            Item = current with
            {
                Title = $"{current.Title} updated",
                Priority = Priority.High,
                Status = TaskItemStatus.InProgress
            }
        };
        using var updateResponse = await httpClient.PutAsJsonAsync($"{TaskItemsPath}/{id}", updateRequest, JsonOptions, ct);
        if (updateResponse.StatusCode != HttpStatusCode.OK) return false;

        using var deleteResponse = await httpClient.DeleteAsync($"{TaskItemsPath}/{id}", ct);
        if (deleteResponse.StatusCode != HttpStatusCode.NoContent) return false;

        using var verifyDeletedResponse = await httpClient.GetAsync($"{TaskItemsPath}/{id}", ct);
        return verifyDeletedResponse.StatusCode == HttpStatusCode.NotFound;
    }
}
