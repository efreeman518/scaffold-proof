using EF.AI.Testing;
using EF.FlowEngine;
using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Clients;
using EF.FlowEngine.Clients.AI;
using EF.FlowEngine.Impl;
using EF.FlowEngine.Model;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System.Text.Json;

namespace Test.Unit.AI;

/// <summary>
/// Hermetic in-memory workflow tests for the two shipped FlowEngine workflow definitions.
/// No Docker, no real SQL, no live model - all connectors are stubbed deterministically.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class FlowEngineWorkflowTests
{
    private static readonly Guid TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001");

    private static readonly JsonSerializerOptions WebJsonOptions = new(JsonSerializerDefaults.Web);


    // ── ai-task-triage ─────────────────────────────────────────────────────────

    [TestMethod]
    public async Task TriageWorkflow_HappyPath_CompletesApplied()
    {
        var taskId = Guid.NewGuid();
        int patchHits = 0;

        using var provider = BuildProvider(
            chatReply: """{"suggestedPriority":"High","suggestedCategory":"Bug","confidence":0.9}""",
            apiHandler: (req, _) =>
            {
                if (string.Equals(req.Method, "PATCH", StringComparison.OrdinalIgnoreCase)) patchHits++;
                return Task.FromResult(OkResponse());
            });

        var instance = await StartAsync(provider, "ai-task-triage", taskId);

        Assert.AreEqual(ExecStatus.Completed, instance.Status, instance.Error?.Message);
        Assert.AreEqual("n-output-ok", instance.CurrentNodeId,
            $"Unexpected node. Error: {instance.Error?.Message}");
        Assert.AreEqual("applied", GetOutputString(instance, "decision"));
        Assert.AreEqual(1, patchHits, "expected one PATCH to apply priority");
    }

    [TestMethod]
    public async Task TriageWorkflow_BadModelOutput_RoutesToFaultedNode()
    {
        var taskId = Guid.NewGuid();

        using var provider = BuildProvider(
            chatReply: "not-valid-json",
            apiHandler: (_, _) => Task.FromResult(OkResponse()));

        var instance = await StartAsync(provider, "ai-task-triage", taskId);

        Assert.AreEqual(ExecStatus.Completed, instance.Status, instance.Error?.Message);
        Assert.AreEqual("n-faulted", instance.CurrentNodeId,
            $"Expected fault-path routing. Error: {instance.Error?.Message}");
    }

    /// <summary>
    /// The node retryPolicy is the only retry owner: a PATCH answered 503 is resent by the engine and the instance
    /// completes (WorkflowDefinitionValidityTests asserts the definitions raise no warning).
    /// </summary>
    [TestMethod]
    public async Task TriageWorkflow_Patch503ThenOk_NodeRetryPolicyResends()
    {
        var taskId = Guid.NewGuid();
        int patchHits = 0;

        using var provider = BuildProvider(
            chatReply: """{"suggestedPriority":"High","suggestedCategory":"Bug","confidence":0.9}""",
            apiHandler: (req, _) =>
            {
                if (!string.Equals(req.Method, "PATCH", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(OkResponse());
                return Task.FromResult(++patchHits == 1 ? ErrorResponse(503) : OkResponse());
            });

        var instance = await StartAsync(provider, "ai-task-triage", taskId);

        Assert.AreEqual(ExecStatus.Completed, instance.Status, instance.Error?.Message);
        Assert.AreEqual("n-output-ok", instance.CurrentNodeId, $"Unexpected node. Error: {instance.Error?.Message}");
        Assert.AreEqual(2, patchHits, "the 503 must be resent once by the node retryPolicy");
    }

    /// <summary>D-032: a 412 means the precondition is stale, so the engine never resends the PATCH.</summary>
    [TestMethod]
    public async Task TriageWorkflow_Patch412_IsNotResent()
    {
        var taskId = Guid.NewGuid();
        int patchHits = 0;

        using var provider = BuildProvider(
            chatReply: """{"suggestedPriority":"High","suggestedCategory":"Bug","confidence":0.9}""",
            apiHandler: (req, _) =>
            {
                if (!string.Equals(req.Method, "PATCH", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(OkResponse());
                patchHits++;
                return Task.FromResult(ErrorResponse(412));
            });

        var instance = await StartAsync(provider, "ai-task-triage", taskId);

        Assert.AreEqual(1, patchHits, "a 412 must not be resent");
        Assert.AreEqual("n-faulted", instance.CurrentNodeId, $"Expected fault-path routing. Error: {instance.Error?.Message}");
    }

    // ── ai-task-decomposer ──────────────────────────────────────────────────────

    [TestMethod]
    public async Task DecomposerWorkflow_HappyPath_CreatesSubtasksAndCompletes()
    {
        var taskId = Guid.NewGuid();
        int postHits = 0;

        using var provider = BuildProvider(
            chatReply: """{"subtasks":[{"title":"Sub A","estimateHours":1},{"title":"Sub B","estimateHours":2}]}""",
            apiHandler: (req, _) =>
            {
                if (string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase)) postHits++;
                return Task.FromResult(CreatedResponse());
            });

        var instance = await StartAsync(provider, "ai-task-decomposer", taskId);

        Assert.AreEqual(ExecStatus.Completed, instance.Status, instance.Error?.Message);
        Assert.AreEqual("n-output-ok", instance.CurrentNodeId,
            $"Unexpected node. Error: {instance.Error?.Message}");
        Assert.AreEqual("decomposed", GetOutputString(instance, "decision"));
        Assert.AreEqual(2, postHits, "expected one POST per subtask");
    }

    /// <summary>
    /// The loop-body create POST is keyed per iteration: each of three iterations is answered 502 once and resent by
    /// the node retryPolicy with the same Idempotency-Key, and the three iterations send three distinct keys.
    /// </summary>
    [TestMethod]
    public async Task DecomposerWorkflow_LoopBodyPost502ThenCreated_ResendsEachIterationWithItsOwnKey()
    {
        var taskId = Guid.NewGuid();
        var keys = new List<string>();

        using var provider = BuildProvider(
            chatReply: """{"subtasks":[{"title":"Sub A","estimateHours":1},{"title":"Sub B","estimateHours":2},{"title":"Sub C","estimateHours":3}]}""",
            apiHandler: (req, _) =>
            {
                if (!string.Equals(req.Method, "POST", StringComparison.OrdinalIgnoreCase)) return Task.FromResult(OkResponse());
                string? key = null;
                Assert.IsTrue(req.Headers?.TryGetValue("Idempotency-Key", out key) == true && !string.IsNullOrEmpty(key),
                    "the loop-body POST must send the generated Idempotency-Key header");
                var firstAttempt = !keys.Contains(key);
                keys.Add(key);
                return Task.FromResult(firstAttempt ? ErrorResponse(502) : CreatedResponse());
            });

        var instance = await StartAsync(provider, "ai-task-decomposer", taskId);

        Assert.AreEqual(ExecStatus.Completed, instance.Status, instance.Error?.Message);
        Assert.AreEqual("n-output-ok", instance.CurrentNodeId, $"Unexpected node. Error: {instance.Error?.Message}");
        Assert.HasCount(6, keys, "each iteration: one 502 plus one resend");
        Assert.HasCount(3, keys.Distinct(StringComparer.Ordinal).ToList(), "each iteration sends its own key");
        CollectionAssert.AreEqual(new[] { keys[0], keys[0], keys[2], keys[2], keys[4], keys[4] }, keys,
            "a resend carries its iteration's key");
    }

    [TestMethod]
    public async Task DecomposerWorkflow_BadModelOutput_RoutesToFailedNode()
    {
        var taskId = Guid.NewGuid();

        using var provider = BuildProvider(
            chatReply: "not-valid-json",
            apiHandler: (_, _) => Task.FromResult(OkResponse()));

        var instance = await StartAsync(provider, "ai-task-decomposer", taskId);

        Assert.AreEqual(ExecStatus.Completed, instance.Status, instance.Error?.Message);
        Assert.AreEqual("n-output-failed", instance.CurrentNodeId,
            $"Expected failure-path routing. Error: {instance.Error?.Message}");
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    // Loads the shipped workflow JSON files from disk into the engine's in-memory registry.
    // UseJsonFileWorkflowRegistry cannot be used directly because the engine pins to the versioned
    // file ({id}@{version}.json) which only exists after SaveAsync writes it.
    private static async Task SeedWorkflowsAsync(IServiceProvider provider)
    {
        var fileReg = new JsonFileWorkflowRegistry("Workflows");
        var engineReg = provider.GetRequiredService<IWorkflowRegistry>();
        foreach (var id in new[] { "ai-task-triage", "ai-task-decomposer" })
        {
            var def = await fileReg.GetAsync(id, version: null);
            if (def is not null) await engineReg.SaveAsync(def);
        }
    }

    private static ServiceProvider BuildProvider(
        string chatReply,
        Func<ClientRequest, CancellationToken, Task<ClientResponse>> apiHandler)
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));

        var fe = services.AddFlowEngine()
            .UseAllInMemoryProviders();

        services.AddSingleton<IChatClient>(new FakeChatClient(chatReply));
        fe.AddChatClientAgentClient("ai-agent", sp => sp.GetRequiredService<IChatClient>());

        // Stub the message connector - no Service Bus in unit tests.
        services.AddSingleton<IFlowClient>(new DelegatingMessageClient(
            "integration-events",
            (_, _) => Task.FromResult(new MessageResult { Sent = true, Outcome = DecisionOutcome.Match })));

        // Stub the taskflow-api HTTP connector directly as IRequestResponseClient to avoid
        // any resilience-pipeline setup or HttpClient base-address wiring in unit tests.
        services.AddSingleton<IFlowClient>(new FakeApiClient("taskflow-api", apiHandler));

        return services.BuildServiceProvider();
    }

    private static async Task<ExecutionInstance> StartAsync(
        IServiceProvider provider,
        string workflowId,
        Guid taskId)
    {
        await SeedWorkflowsAsync(provider);
        var engine = provider.GetRequiredService<IFlowEngine>();
        return await engine.StartAsync(new StartRequest
        {
            WorkflowId = workflowId,
            TenantId = TenantId.ToString(),
            Params = new Dictionary<string, ContextValue>
            {
                ["tenantId"] = Param(TenantId.ToString()),
                ["taskId"] = Param(taskId.ToString()),
                ["description"] = Param("A sample task description for deterministic workflow tests.")
            }
        });
    }

    private static JsonContextValue Param(string value)
        => new() { Value = JsonSerializer.SerializeToElement(value) };

    private static string? GetOutputString(ExecutionInstance instance, string key)
    {
        Assert.IsInstanceOfType<JsonContextValue>(instance.Output, "expected JsonContextValue output");
        var dict = ((JsonContextValue)instance.Output!).Value
            .Deserialize<Dictionary<string, JsonElement>>(WebJsonOptions);
        return dict?.TryGetValue(key, out var el) == true ? el.GetString() : null;
    }

    private static ClientResponse OkResponse() => new()
    {
        StatusCode = 200,
        Outcome = DecisionOutcome.Match,
        Body = JsonSerializer.SerializeToElement(new { }),
        Headers = new Dictionary<string, string>()
    };

    private static ClientResponse CreatedResponse() => new()
    {
        StatusCode = 201,
        Outcome = DecisionOutcome.Match,
        Body = JsonSerializer.SerializeToElement(new { }),
        Headers = new Dictionary<string, string>()
    };

    private static ClientResponse ErrorResponse(int statusCode) => new()
    {
        StatusCode = statusCode,
        Outcome = DecisionOutcome.Error,
        Body = JsonSerializer.SerializeToElement(new { }),
        Headers = new Dictionary<string, string>()
    };

    // Stub IRequestResponseClient used in place of the resilience-wrapped HttpClient.
    private sealed class FakeApiClient(
        string clientRef,
        Func<ClientRequest, CancellationToken, Task<ClientResponse>> handler)
        : IRequestResponseClient
    {
        public string ClientRef => clientRef;
        public Task ValidateAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task<ClientResponse> ExecuteAsync(ClientRequest request, CancellationToken ct)
            => handler(request, ct);
    }
}
