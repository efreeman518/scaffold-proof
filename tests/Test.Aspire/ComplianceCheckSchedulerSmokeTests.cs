using Aspire.Hosting.Testing;
using Microsoft.EntityFrameworkCore;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;
using Test.Support.Hosting;
using TickerQ.Utilities.Entities;

namespace Test.Aspire;

/// <summary>
/// The compliance-check start path in the running graph (D-075): a due ticker for the Scheduler's ComplianceCheck job
/// starts compliance-check for the scaffold tenant, which has an open task tagged "compliance" with text evidence, and
/// the compliance-check-item child that the instance starts reads that evidence (its document node takes the Match
/// edge). That read is refused to an instance without a tenant, so it shows the job set the instance tenant, and the
/// child's attachment search shows the Scheduler's self-calls reach the Api.
/// Aspire tier: needs the Scheduler in the graph (TASKFLOW_ASPIRE_SCHEDULER_AVAILABLE=true).
/// </summary>
[TestClass]
[TestCategory("Aspire")]
[DoNotParallelize]
public class ComplianceCheckSchedulerSmokeTests
{
    // ScaffoldPrincipal.TenantId: every Api request, the workflow's self-calls included, authenticates as this tenant.
    private const string ScaffoldTenant = "00000000-0000-0000-0000-000000000001";
    private static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    public TestContext TestContext { get; set; } = null!;

    /// <summary>Boots the Aspire graph lazily; teardown is owned by <c>AspireMeshLifecycle</c>.</summary>
    [ClassInitialize]
    public static Task ClassInit(TestContext context) => AspireTestHost.EnsureStartedAsync(context);

    [TestInitialize]
    public Task TestSetup()
    {
        if (Environment.GetEnvironmentVariable("TASKFLOW_ASPIRE_SCHEDULER_AVAILABLE") != "true")
            Assert.Inconclusive("No Scheduler runs in this graph. Set TASKFLOW_ASPIRE_SCHEDULER_AVAILABLE=true to add it.");
        return AspireTestHost.WaitForResourceHealthyAsync("taskflowscheduler", TestContext.CancellationToken);
    }

    [TestMethod]
    [Timeout(900_000, CooperativeCancellation = true)]
    public async Task DueComplianceCheckTicker_StartsTheTenantsInstance_AndItsChildReadsTheEvidence()
    {
        var ct = TestContext.CancellationToken;
        var client = AspireTestHost.AspireApp!.CreateHttpClient("taskflowapi");

        var tagId = await CreateAsync(client, "/api/v1/tags", new { name = "compliance", color = "#336699" }, ct);
        var taskId = await CreateAsync(client, "/api/v1/task-items",
            new { title = "Compliance smoke task", priority = "Medium", dueDate = DateTimeOffset.UtcNow.AddDays(1) }, ct);
        using (var associate = await client.PostAsync($"/api/v1/task-items/{taskId}/tags/{tagId}", content: null, ct))
        {
            Assert.AreEqual(HttpStatusCode.Created, associate.StatusCode, await associate.Content.ReadAsStringAsync(ct));
        }
        var evidenceId = await UploadEvidenceAsync(client, taskId, ct);

        // A due one-off ticker for the job, as the cron occurrence would be; the Scheduler picks it up from the store.
        await using (var tickerQ = CreateTickerQContext())
        {
            var now = DateTime.UtcNow;
            tickerQ.Set<TimeTickerEntity>().Add(new TimeTickerEntity
            {
                Id = Guid.NewGuid(),
                Function = "ComplianceCheck",
                ExecutionTime = now,
            });
            await tickerQ.SaveChangesAsync(ct);
        }

        var parent = await WaitForAsync(client, "compliance-check", i => Field(i, "tenantId") == ScaffoldTenant && IsTerminal(i), ct);
        Assert.AreEqual("n-output-ok", Field(parent, "currentNodeId"), parent.ToString());

        var listed = await WaitForAsync(client, "compliance-check-item",
            i => Field(i, "parentInstanceId") == Field(parent, "instanceId") && IsTerminal(i), ct);
        // The list strips history; the instance read carries it.
        var child = await client.GetFromJsonAsync<JsonElement>($"/api/flowengine/instances/{Field(listed, "instanceId")}", ct);
        var path = Path(child);
        TestContext.WriteLine($"evidence {evidenceId}; child path {path}");
        Assert.AreEqual(ScaffoldTenant, Field(child, "tenantId"), "the child inherits the job's tenant");
        StringAssert.Contains(path, "n-fetch-evidence:Match", "the child reads the task's evidence as the started tenant. " + child);
        Assert.AreEqual("n-done", Field(child, "currentNodeId"), child.ToString());
    }

    private static async Task<Guid> CreateAsync(HttpClient client, string path, object item, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(path, new { item }, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, $"POST {path}: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("item").GetProperty("id").GetGuid();
    }

    private static async Task<Guid> UploadEvidenceAsync(HttpClient client, Guid taskId, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent("certificate expires next week"u8.ToArray());
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
        content.Add(file, "file", "evidence.txt");
        content.Add(new StringContent(((int)AttachmentOwnerType.TaskItem).ToString(System.Globalization.CultureInfo.InvariantCulture)), "ownerType");
        content.Add(new StringContent(taskId.ToString()), "ownerId");
        using var response = await client.PostAsync("/api/v1/attachments/upload", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, $"upload: {body}");
        using var json = JsonDocument.Parse(body);
        return json.RootElement.GetProperty("item").GetProperty("id").GetGuid();
    }

    // Polls the admin instance list until an instance of the workflow matches, within the budget.
    private static async Task<JsonElement> WaitForAsync(
        HttpClient client, string workflowId, Func<JsonElement, bool> match, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + Budget;
        var last = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/flowengine/instances?workflowId={workflowId}&take=50", ct);
            last = await response.Content.ReadAsStringAsync(ct);
            Assert.IsTrue(response.IsSuccessStatusCode, $"{workflowId} list: {(int)response.StatusCode} {last}");
            using var json = JsonDocument.Parse(last);
            var items = json.RootElement.ValueKind == JsonValueKind.Array
                ? json.RootElement
                : json.RootElement.EnumerateObject().First(p => p.Value.ValueKind == JsonValueKind.Array).Value;
            foreach (var item in items.EnumerateArray())
            {
                if (match(item)) return item.Clone();
            }

            await Task.Delay(PollInterval, ct);
        }

        Assert.Fail($"No matching {workflowId} instance within {Budget}. Last list: {last}");
        throw new InvalidOperationException("Unreachable");
    }

    private static bool IsTerminal(JsonElement instance) =>
        Field(instance, "currentNodeId") is "n-output-ok" or "n-output-failed" or "n-done" or "n-failed";

    private static string? Field(JsonElement element, string name) =>
        element.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value
            is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    // nodeId:outcome for each history entry, so a failed assertion shows the route the child took.
    private static string Path(JsonElement instance)
    {
        var history = instance.EnumerateObject()
            .FirstOrDefault(p => string.Equals(p.Name, "history", StringComparison.OrdinalIgnoreCase)).Value;
        return history.ValueKind != JsonValueKind.Array
            ? "?"
            : string.Join(">", history.EnumerateArray().Select(h => Field(h, "nodeId") + ":" + Field(h, "outcome")));
    }

    private static TaskFlowTickerQDbContext CreateTickerQContext() =>
        new(new DbContextOptionsBuilder<TaskFlowTickerQDbContext>()
            .UseTaskFlowProvider(new TaskFlowProviderOptions(
                TestHostingLane.DatabaseProvider,
                AspireTestHost.ConnectionString,
                TaskFlowTickerQDbContext.MigrationHistoryTable,
                TaskFlowTickerQDbContext.SchemaName))
            .Options);
}
