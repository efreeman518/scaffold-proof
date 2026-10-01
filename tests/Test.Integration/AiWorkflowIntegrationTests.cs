using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Definition;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// Component-tier integration tests for the two shipped FlowEngine workflows. Each test boots the real
/// TaskFlow.Api host in-process against the SQL container, starts a workflow through the public
/// FlowEngine API, lets the background engine drive it to a terminal node, and then asserts the REAL
/// database side effects the workflow produced through its self-calls (priority patched / child tasks
/// created) - not just the engine's terminal state. Inconclusive without a container runtime; fails when the
/// SQL container did not start.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public sealed class AiWorkflowIntegrationTests
{
    // Scaffold auth identity (ScaffoldPrincipal, EF.Auth fixed principal) the in-process host authenticates every request as.
    private const string TenantId = "00000000-0000-0000-0000-000000000001";
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(60);
    // The engine drives the workflow in the background and it terminates within a second or two, so a
    // calm poll cadence reaches the terminal in a handful of requests and stays under the API rate limit.
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);

    private static readonly string[] TerminalNodes =
        ["n-output-ok", "n-output-rejected", "n-output-failed", "n-faulted"];

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task TriageWorkflow_AppliesSuggestedPriority_ThroughRealApi()
    {
        SkipIfNoSql();
        var ct = TestContext.CancellationToken;
        var connectionString = await IsolatedMigratedConnectionStringAsync(ct);

        // Non-Critical suggestion -> no human quorum -> PATCH priority -> publish -> n-output-ok.
        using var factory = new FlowEngineWorkflowApiFactory(
            connectionString,
            _ => """{"suggestedPriority":"High","suggestedCategory":"Bug","confidence":0.9}""");
        using var client = factory.CreateClient();

        var taskId = await CreateTaskAsync(client, "Triage integration task", priority: 1 /* Low */, ct);
        var priorityBefore = await ReadTaskPriorityAsync(client, taskId, ct);

        var instanceId = await StartWorkflowAsync(client, "ai-task-triage", new Dictionary<string, object?>
        {
            ["tenantId"] = TenantId,
            ["taskId"] = taskId.ToString(),
            ["description"] = "Classify this implementation task."
        }, ct);

        var (node, body) = await WaitForTerminalAsync(client, instanceId, ct);

        Assert.AreEqual("n-output-ok", node, $"Triage should reach the applied-priority terminal via the real PATCH self-call. Instance: {Truncate(body)}");
        var priorityAfter = await ReadTaskPriorityAsync(client, taskId, ct);
        Assert.AreNotEqual(priorityBefore, priorityAfter, "Priority should have been changed by the workflow's PATCH self-call.");
        Assert.Contains("High", priorityAfter, "Workflow should have applied the agent-suggested High priority.");
    }

    [TestMethod]
    public async Task DecomposerWorkflow_CreatesChildTasks_ThroughRealApi()
    {
        SkipIfNoSql();
        var ct = TestContext.CancellationToken;
        var connectionString = await IsolatedMigratedConnectionStringAsync(ct);

        using var factory = new FlowEngineWorkflowApiFactory(
            connectionString,
            _ => """{"subtasks":[{"title":"Sub A","estimateHours":1},{"title":"Sub B","estimateHours":2}]}""");
        using var client = factory.CreateClient();

        var parentId = await CreateTaskAsync(client, "Decomposer integration task", priority: 2 /* Medium */, ct);

        var instanceId = await StartWorkflowAsync(client, "ai-task-decomposer", new Dictionary<string, object?>
        {
            ["tenantId"] = TenantId,
            ["taskId"] = parentId.ToString(),
            ["description"] = "Build a login page with validation and password reset."
        }, ct);

        var (node, body) = await WaitForTerminalAsync(client, instanceId, ct);

        Assert.AreEqual("n-output-ok", node, $"Decomposer should reach the decomposed terminal after creating child tasks. Instance: {Truncate(body)}");
        var childCount = await CountChildrenAsync(client, parentId, ct);
        Assert.AreEqual(2, childCount, "Workflow should have created one child TaskItem per proposed subtask via POST self-calls.");
    }

    /// <summary>
    /// D-074 end to end through the engine: the shipped keyed POST comment node (triage <c>n-compensate-reject</c>)
    /// runs as the only node of a probe workflow. Its first POST reaches the API and adds the comment, but the
    /// response is lost and the engine sees a 502. The node retries the ambiguous status with the same generated
    /// Idempotency-Key, the API maps the key to the stored comment id and replays it, so the task has one comment.
    /// (The shipped loop-body POST nodes send the key too, but EF.FlowEngine 1.0.199 does not apply a node
    /// retryPolicy inside a loop body, so their resend comes only from lease recovery.)
    /// </summary>
    [TestMethod]
    public async Task KeyedCommentPost_RetriedOn502_AddsOneComment()
    {
        SkipIfNoSql();
        var ct = TestContext.CancellationToken;
        var connectionString = await IsolatedMigratedConnectionStringAsync(ct);
        var lostResponse = new LostResponseState();

        using var factory = new FlowEngineWorkflowApiFactory(
            connectionString,
            _ => "{}",
            selfCall => selfCall.AddHttpMessageHandler(() => new LoseFirstCommentResponse(lostResponse)));
        using var client = factory.CreateClient();

        var taskId = await CreateTaskAsync(client, "Keyed comment retry task", priority: 2 /* Medium */, ct);
        await factory.Services.GetRequiredService<IWorkflowRegistry>().SaveAsync(KeyedCommentProbe(), ct);
        var instanceId = await StartWorkflowAsync(client, KeyedCommentProbeId, new Dictionary<string, object?>
        {
            ["tenantId"] = TenantId,
            ["taskId"] = taskId.ToString()
        }, ct);

        var (node, body) = await WaitForTerminalAsync(client, instanceId, ct);

        Assert.AreEqual("n-output-ok", node, $"The 502 must be retried, not fault the node. Instance: {Truncate(body)}");
        Assert.AreEqual(2, lostResponse.CommentPosts, "one lost attempt plus one resend");
        Assert.IsFalse(string.IsNullOrWhiteSpace(lostResponse.FirstKey), "the keyed node must send the generated key");
        Assert.AreEqual(lostResponse.FirstKey, lostResponse.RetryKey, "the resend must carry the same Idempotency-Key");
        Assert.AreEqual(1, await CountCommentsAsync(client, taskId, ct), "the resend must replay the comment, not add a second");
    }

    private const string KeyedCommentProbeId = "idempotency-key-probe";

    // A one-node workflow whose node is the shipped triage reject-comment node, unchanged except for its edges.
    private static WorkflowDefinition KeyedCommentProbe()
    {
        var triage = JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Workflows", "ai-task-triage.json")))!;
        var post = triage["nodes"]!["n-compensate-reject"]!.DeepClone();
        post["edges"] = JsonNode.Parse("""[{ "on": ["Match"], "nextNodeId": "n-output-ok" }, { "on": ["Error"], "nextNodeId": "n-faulted" }]""");
        var probe = new JsonObject
        {
            ["id"] = KeyedCommentProbeId,
            ["version"] = "1.0.0",
            ["status"] = "Active",
            ["entryNodeId"] = "n-compensate-reject",
            ["nodes"] = new JsonObject
            {
                ["n-compensate-reject"] = post,
                ["n-output-ok"] = JsonNode.Parse("""{ "id": "n-output-ok", "type": "output", "config": {} }"""),
                ["n-faulted"] = JsonNode.Parse("""{ "id": "n-faulted", "type": "output", "config": {} }""")
            }
        };
        return WorkflowDefinitionBuilder.FromJson(probe.ToJsonString()).Build();
    }

    private static async Task<int> CountCommentsAsync(HttpClient client, Guid taskId, CancellationToken ct)
    {
        using var response = await client.GetAsync($"/api/v1/task-items/{taskId}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"Get task failed: {Truncate(body)}");
        using var payload = JsonDocument.Parse(body);
        return payload.RootElement.GetProperty("item").GetProperty("comments").GetArrayLength();
    }

    private sealed class LostResponseState
    {
        public int CommentPosts;
        public string? FirstKey;
        public string? RetryKey;
    }

    // Forwards the first keyed comment POST to the API (which commits it), then answers the engine with a 502 as if
    // the response had been lost; later requests pass through unchanged. State is shared because the client factory
    // may build more than one handler chain.
    private sealed class LoseFirstCommentResponse(LostResponseState state) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isComment = request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.EndsWith("/comments", StringComparison.Ordinal);
            if (!isComment) return await base.SendAsync(request, cancellationToken);

            var key = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null;
            var count = Interlocked.Increment(ref state.CommentPosts);
            var response = await base.SendAsync(request, cancellationToken);
            if (count == 1)
            {
                state.FirstKey = key;
                Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, "the lost attempt must have added the comment");
                response.Dispose();
                return new HttpResponseMessage(HttpStatusCode.BadGateway) { RequestMessage = request };
            }

            if (count == 2) state.RetryKey = key;
            return response;
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────────────

    private static void SkipIfNoSql()
    {
        IntegrationTestSetup.AssertAvailable("SQL", DbContainerFixture.StartupError);
    }

    // Runtime hosts do not migrate. Component test owns schema prep before API factory starts.
    private static async Task<string> IsolatedMigratedConnectionStringAsync(CancellationToken ct)
    {
        var connectionString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("TaskFlow_AiWorkflow", ct);

        await using var trxn = DbContainerFixture.CreateTrxnContext(connectionString);
        await trxn.Database.MigrateAsync(ct);

        await using var flowEngine = DbContainerFixture.CreateFlowEngineContext(connectionString);
        await flowEngine.Database.MigrateAsync(ct);

        return connectionString;
    }

    private static async Task<Guid> CreateTaskAsync(HttpClient client, string title, int priority, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/task-items",
            new { item = new { title, description = "Created by FlowEngine workflow integration tests.", priority } },
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, $"Create failed: {Truncate(body)}");
        using var payload = JsonDocument.Parse(body);
        return payload.RootElement.GetProperty("item").GetProperty("id").GetGuid();
    }

    private static async Task<string> ReadTaskPriorityAsync(HttpClient client, Guid taskId, CancellationToken ct)
    {
        using var response = await client.GetAsync($"/api/v1/task-items/{taskId}", ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"Get task failed: {Truncate(body)}");
        using var payload = JsonDocument.Parse(body);
        var priority = payload.RootElement.GetProperty("item").GetProperty("priority");
        // String-enum or numeric serialization - normalize to a string for comparison either way.
        return priority.ValueKind == JsonValueKind.String ? priority.GetString()! : priority.GetRawText();
    }

    private static async Task<int> CountChildrenAsync(HttpClient client, Guid parentId, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/task-items/search",
            new { pageSize = 50, filter = new { parentTaskItemId = parentId.ToString() } },
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"Search failed: {Truncate(body)}");
        using var payload = JsonDocument.Parse(body);
        return payload.RootElement.GetProperty("items").GetArrayLength();
    }

    private static async Task<string> StartWorkflowAsync(
        HttpClient client, string workflowId, Dictionary<string, object?> parameters, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/flowengine/instances/start",
            new Dictionary<string, object?>
            {
                ["workflowId"] = workflowId,
                ["tenantId"] = TenantId,
                ["correlationId"] = Guid.NewGuid().ToString("N"),
                ["params"] = parameters
            },
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.IsTrue(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.Created,
            $"Workflow start failed: {(int)response.StatusCode}. {Truncate(body)}");
        using var payload = JsonDocument.Parse(body);
        var instanceId = FindStringProperty(payload.RootElement, "instanceId");
        Assert.IsFalse(string.IsNullOrWhiteSpace(instanceId), $"No instanceId in start response: {Truncate(body)}");
        return instanceId!;
    }

    // Polls the instance until the engine parks it on one of the workflow's terminal output nodes.
    // Returns the terminal node id and the final instance body (the body carries the fault reason when
    // the workflow lands on n-faulted / n-output-failed).
    private static async Task<(string Node, string Body)> WaitForTerminalAsync(
        HttpClient client, string instanceId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.Add(PollTimeout);
        var lastBody = string.Empty;

        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/flowengine/instances/{instanceId}", ct);
            // Rate limiting / transient unavailability while polling is not a test failure - back off and retry.
            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                await Task.Delay(PollInterval, ct);
                continue;
            }

            lastBody = await response.Content.ReadAsStringAsync(ct);
            Assert.IsTrue(response.IsSuccessStatusCode, $"Instance read failed: {(int)response.StatusCode}. {Truncate(lastBody)}");

            using var document = JsonDocument.Parse(lastBody);
            var node = FindStringProperty(document.RootElement, "currentNodeId");
            if (node != null && TerminalNodes.Contains(node, StringComparer.OrdinalIgnoreCase))
                return (node, lastBody);

            await Task.Delay(PollInterval, ct);
        }

        Assert.Fail($"Timed out waiting for a terminal node. Last instance body: {Truncate(lastBody)}");
        throw new InvalidOperationException("Unreachable");
    }

    private static string? FindStringProperty(JsonElement element, string propertyName)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase)
                        && property.Value.ValueKind == JsonValueKind.String)
                        return property.Value.GetString();

                    var nested = FindStringProperty(property.Value, propertyName);
                    if (nested != null) return nested;
                }
                return null;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    var nested = FindStringProperty(item, propertyName);
                    if (nested != null) return nested;
                }
                return null;
            default:
                return null;
        }
    }

    private static string Truncate(string value) => value.Length <= 1500 ? value : value[..1500] + "...";
}
