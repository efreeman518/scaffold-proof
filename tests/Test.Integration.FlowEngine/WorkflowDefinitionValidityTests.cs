using EF.FlowEngine.Definition;
using EF.FlowEngine.Definition.NodeConfigs;
using EF.FlowEngine.Impl;
using EF.FlowEngine.Model;
using System.Text.Json;

namespace Test.Integration.FlowEngine;

// Tier 1+2: Validate each shipped workflow JSON deserializes, passes WorkflowDefinitionValidator,
// and round-trips through an in-memory IWorkflowRegistry (which re-runs the validator on SaveAsync).
//
// These tests catch most authoring mistakes that would otherwise only surface at first-instance-start
// in dev - unknown node types, dangling edge.nextNodeId references, missing entryNodeId, malformed
// outputSchema / paramsSchema JSON.
[TestClass]
public class WorkflowDefinitionValidityTests
{
    private const string TriageId = "ai-task-triage";
    private const string DecomposerId = "ai-task-decomposer";
    private const string ComplianceId = "compliance-check";

    // The POST nodes outside a loop body. The loop-body POST nodes (n-loop-create-one, n-mark-resolved, n-remind)
    // send no key and declare no retryPolicy until FlowEngine keys and retries each iteration.
    private static readonly HashSet<string> KeyedPostNodes = ["n-compensate-reject"];

    /// <summary>Verifies all workflows behavior and protects the expected test contract.</summary>
    public static IEnumerable<object[]> AllWorkflows() =>
    [
        ["ai-task-triage.json",      TriageId,      "1.0.0"],
        ["ai-task-decomposer.json",  DecomposerId,  "1.0.0"],
        ["compliance-check.json",    ComplianceId,  "1.0.0"],
    ];

    // Canonical options for FlowEngine workflow JSON - camelCase, string-named enums with
    // integer fallback. Both the seeding service and WorkflowDefinitionBuilder.FromJson use
    // this same instance; reusing it here keeps the test serializer in lock-step with runtime.
    private static readonly JsonSerializerOptions JsonOpts = WorkflowDefinitionJsonOptions.Default;

    /// <summary>Verifies each workflow JSON deserializes behavior and protects the expected test contract.</summary>
    [TestMethod]
    [DynamicData(nameof(AllWorkflows))]
    [TestCategory("Integration")]
    public void Each_Workflow_Json_Deserializes(string fileName, string expectedId, string expectedVersion)
    {
        var def = JsonSerializer.Deserialize<WorkflowDefinition>(ReadWorkflowFile(fileName), JsonOpts)!;

        Assert.AreEqual(expectedId, def.Id, "Workflow Id mismatch");
        Assert.AreEqual(expectedVersion, def.Version, "Workflow Version mismatch");
        Assert.IsFalse(string.IsNullOrWhiteSpace(def.EntryNodeId), "EntryNodeId missing");
        Assert.IsTrue(def.Nodes.ContainsKey(def.EntryNodeId), "EntryNodeId does not reference an existing node");
    }

    /// <summary>Verifies each workflow passes definition validator behavior and protects the expected test contract.</summary>
    [TestMethod]
    [DynamicData(nameof(AllWorkflows))]
    [TestCategory("Integration")]
    public void Each_Workflow_Passes_DefinitionValidator(string fileName, string _id, string _version)
    {
        var def = JsonSerializer.Deserialize<WorkflowDefinition>(ReadWorkflowFile(fileName), JsonOpts)!;

        // ValidateAndThrow surfaces validator errors (unknown node types, dangling edges,
        // missing required fields, malformed JSON schemas) as a typed exception.
        WorkflowDefinitionValidator.ValidateAndThrow(def);
    }

    /// <summary>
    /// Verifies the shipped definitions raise no advisory warning under the FlowEngine retry validation
    /// (for example an unsafe node listing an ambiguous status without an idempotency header).
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(AllWorkflows))]
    [TestCategory("Integration")]
    public void Each_Workflow_Has_No_DefinitionValidator_Warnings(string fileName, string _id, string _version)
    {
        var def = JsonSerializer.Deserialize<WorkflowDefinition>(ReadWorkflowFile(fileName), JsonOpts)!;

        var warnings = WorkflowDefinitionValidator.GetWarnings(def);

        Assert.IsEmpty(warnings, $"{fileName}: {string.Join(" | ", warnings)}");
    }

    /// <summary>
    /// The node RetryPolicy is the only retry owner for the taskflow-api calls (the resilient HTTP adapter sends
    /// once per attempt), so every integration node outside a loop body declares one with exponential backoff. The
    /// top-level POST node sends the engine-generated key in the <c>Idempotency-Key</c> header the API deduplicates
    /// (D-074), which opts it into the full inherited status list (408, 409, 429, 500, 502, 503, 504) and transport
    /// retries. The If-Match: * PATCH nodes send no key and keep the engine's 409/429/503 unsafe-method default. No
    /// node overrides the inherited list, and no list carries 412 (D-032: a stale precondition is never resent).
    /// <para>
    /// A loop-body node declares neither: EF.FlowEngine 1.0.199 applies no node retryPolicy inside a loop body, so a
    /// policy there is dead config, and it generates the same key for every iteration of a loop-body node, so the
    /// API would replay the first iteration's row for the next one. Revisit both when FlowEngine keys each iteration
    /// and retries loop-body nodes (D-074 records the remaining lease-recovery duplicate risk).
    /// </para>
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(AllWorkflows))]
    [TestCategory("Integration")]
    public void Each_Integration_Node_Declares_Exponential_RetryPolicy_And_Only_The_TopLevel_Post_Is_Keyed(string fileName, string _id, string _version)
    {
        var def = JsonSerializer.Deserialize<WorkflowDefinition>(ReadWorkflowFile(fileName), JsonOpts)!;
        var integrationNodes = def.Nodes.Values.Where(n => n.Type == "integration").ToList();
        Assert.IsNotEmpty(integrationNodes, $"{fileName} has no integration node");
        var loopBody = LoopBodyNodeIds(fileName);

        foreach (var node in integrationNodes)
        {
            var config = JsonSerializer.Deserialize<IntegrationNodeConfig>(JsonSerializer.Serialize(node.Config, JsonOpts), JsonOpts)!;
            Assert.AreEqual("taskflow-api", config.ClientRef, $"{fileName}:{node.Id}");
            Assert.IsNull(config.RetryOnStatusCodes, $"{fileName}:{node.Id} must inherit the default status list");

            if (loopBody.Contains(node.Id))
            {
                Assert.IsNull(node.RetryPolicy, $"{fileName}:{node.Id} is in a loop body, where FlowEngine 1.0.199 applies no retryPolicy");
                Assert.IsNull(config.IdempotencyKeyHeader,
                    $"{fileName}:{node.Id} is in a loop body; FlowEngine 1.0.199 would send one key for every iteration");
                continue;
            }

            var policy = node.RetryPolicy;
            Assert.IsNotNull(policy, $"{fileName}:{node.Id} must declare a retryPolicy");
            Assert.AreEqual(BackoffType.Exponential, policy.Backoff, $"{fileName}:{node.Id}");
            Assert.AreEqual(3, policy.MaxAttempts, $"{fileName}:{node.Id}");
            Assert.DoesNotContain(412, policy.RetryOnHttpStatus, $"{fileName}:{node.Id}");
            var expectedHeader = KeyedPostNodes.Contains(node.Id) ? "Idempotency-Key" : null;
            Assert.AreEqual(expectedHeader, config.IdempotencyKeyHeader,
                $"{fileName}:{node.Id} ({config.Method}): only the top-level POST node sends the key");
        }
    }

    /// <summary>The loop-body integration nodes this suite treats specially are the ones the definitions actually loop over.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    public void LoopBody_Integration_Nodes_Are_The_Expected_Set()
    {
        var found = AllWorkflows()
            .SelectMany(w => LoopBodyNodeIds((string)w[0]).Select(id => (File: (string)w[0], Id: id)))
            .Where(n => IsIntegrationNode(n.File, n.Id))
            .Select(n => n.Id)
            .Order(StringComparer.Ordinal)
            .ToList();

        CollectionAssert.AreEqual(new[] { "n-loop-create-one", "n-mark-resolved", "n-remind" }, found);
    }

    /// <summary>Verifies the FlowEngine validation refuses a 412 retry status, so the D-032 rule cannot regress silently.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    public void DefinitionValidator_Rejects_412_In_A_Node_Retry_List()
    {
        var json = System.Text.Json.Nodes.JsonNode.Parse(ReadWorkflowFile("ai-task-triage.json"))!;
        json["nodes"]!["n-apply-priority"]!["retryPolicy"]!["retryOnHttpStatus"] = new System.Text.Json.Nodes.JsonArray(409, 412);
        var def = json.Deserialize<WorkflowDefinition>(JsonOpts)!;

        Assert.IsNotEmpty(WorkflowDefinitionValidator.Validate(def), "a node retry list containing 412 must fail validation");
    }

    /// <summary>Verifies each workflow node has explicit config for SQL registry serialization behavior and protects the expected test contract.</summary>
    [TestMethod]
    [DynamicData(nameof(AllWorkflows))]
    [TestCategory("Integration")]
    public void Each_Workflow_Node_Has_Explicit_Config_For_SqlRegistrySerialization(string fileName, string _id, string _version)
    {
        using var document = JsonDocument.Parse(ReadWorkflowFile(fileName));
        var nodes = document.RootElement.GetProperty("nodes");

        foreach (var node in nodes.EnumerateObject())
        {
            Assert.IsTrue(
                node.Value.TryGetProperty("config", out _),
                $"{fileName}:{node.Name} must include explicit config, use {{}} when empty.");
        }
    }

    /// <summary>Verifies each workflow round trips through in memory registry behavior and protects the expected test contract.</summary>
    [TestMethod]
    [DynamicData(nameof(AllWorkflows))]
    [TestCategory("Integration")]
    public async Task Each_Workflow_Round_Trips_Through_InMemoryRegistry(string fileName, string id, string version)
    {
        var def = JsonSerializer.Deserialize<WorkflowDefinition>(ReadWorkflowFile(fileName), JsonOpts)!;

        var registry = new InMemoryWorkflowRegistry();
        await registry.SaveAsync(def, TestContext.CancellationToken);

        // Our JSON ships with status=Active, so the explicit transition is a no-op; mirror
        // the seeding service's idempotent pattern (swallow Active->Active) so the test is
        // robust if we ever flip the JSON to ship as Draft.
        try
        {
            await registry.TransitionStatusAsync(id, version, DefinitionStatus.Active, TestContext.CancellationToken);
        }
        catch (InvalidOperationException) { /* already Active */ }

        var loaded = await registry.GetAsync(id, version, TestContext.CancellationToken);
        Assert.IsNotNull(loaded, "Registry returned null for the version just saved");
        Assert.AreEqual(DefinitionStatus.Active, loaded!.Status, "Definition should be Active after save");
        Assert.HasCount(def.Nodes.Count, loaded.Nodes, "Node count drifted on round-trip");
    }

    /// <summary>Verifies workflow definition builder from JSON round trips behavior and protects the expected test contract.</summary>
    [TestMethod]
    [DynamicData(nameof(AllWorkflows))]
    [TestCategory("Integration")]
    public void WorkflowDefinitionBuilder_FromJson_Round_Trips(string fileName, string expectedId, string expectedVersion)
    {
        // WorkflowDefinitionBuilder.FromJson uses WorkflowDefinitionJsonOptions.Default.
        // and fails fast on shape mismatch. The blank-shell bug previously documented here is fixed.
        var def = WorkflowDefinitionBuilder.FromJson(ReadWorkflowFile(fileName)).Build();

        Assert.AreEqual(expectedId, def.Id, "Builder.FromJson should now hydrate Id");
        Assert.AreEqual(expectedVersion, def.Version, "Builder.FromJson should now hydrate Version");
        Assert.IsNotEmpty(def.Nodes, "Builder.FromJson should now hydrate Nodes");
    }

    /// <summary>Verifies all three workflows are present in output behavior and protects the expected test contract.</summary>
    [TestMethod]
    [TestCategory("Integration")]
    public void All_Three_Workflows_Are_Present_In_Output()
    {
        // Guard against the copy-on-build glob in the csproj silently dropping files.
        var dir = Path.Combine(AppContext.BaseDirectory, "Workflows");
        Assert.IsTrue(Directory.Exists(dir), $"Workflows directory missing at {dir}");

        string[] expected = ["ai-task-triage.json", "ai-task-decomposer.json", "compliance-check.json"];
        foreach (var f in expected)
            Assert.IsTrue(File.Exists(Path.Combine(dir, f)), $"Missing workflow file: {f}");
    }

    /// <summary>
    /// The PATCH nodes carry the "headers" config key that EF.FlowEngine 1.0.173 now forwards
    /// (package request 18 shipped: IntegrationNodeConfig.Headers -> IntegrationNodeExecutor ->
    /// ClientRequest.Headers), so If-Match travels through node config with no JSON change.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    public void AiTaskTriage_PatchNodes_CarryForwardCompatibleIfMatchHeader()
    {
        using var document = JsonDocument.Parse(ReadWorkflowFile("ai-task-triage.json"));
        var nodes = document.RootElement.GetProperty("nodes");

        foreach (var nodeId in new[] { "n-apply-priority", "n-revert-priority" })
        {
            var config = nodes.GetProperty(nodeId).GetProperty("config");
            Assert.IsTrue(config.TryGetProperty("headers", out var headers), $"{nodeId} is missing the headers config key.");
            Assert.AreEqual("*", headers.GetProperty("If-Match").GetString());
        }
    }

    /// <summary>
    /// Package request 19 asked for a stable per-iteration id so a retried loop iteration recreates the
    /// same subtask instead of a duplicate. It shipped as <c>LoopNodeConfig.IdAs</c>, but the value
    /// <c>LoopNodeExecutor.IterationId</c> stores is a deterministic RFC 4122 <b>version 5</b> UUID over
    /// instance id + loop node id + index. TaskFlow cannot send that as a create id: GR-17 rejects any
    /// caller-supplied id that is not a UUIDv7, because a hash-ordered key fragments the clustered index
    /// every create lands in - the exact cost GR-17 exists to avoid. The create endpoint answers 400 and
    /// the loop lands on <c>n-output-failed</c>.
    /// <para>
    /// So the body sends no <c>Id</c>, and no <c>Idempotency-Key</c> header either: EF.FlowEngine 1.0.199 generates the
    /// same key for every iteration of a loop-body node, so the API (D-074) would replay the first subtask for the
    /// second. This test fails if someone re-adds the id or the header before the package keys each iteration.
    /// </para>
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    public void AiTaskDecomposer_LoopBody_DoesNotSendThePerIterationIdAsTheSubtaskId()
    {
        using var document = JsonDocument.Parse(ReadWorkflowFile("ai-task-decomposer.json"));
        var nodes = document.RootElement.GetProperty("nodes");

        var loopConfig = nodes.GetProperty("n-create-subtasks").GetProperty("config");
        var loop = loopConfig.Deserialize<LoopNodeConfig>(JsonOpts)!;
        Assert.AreEqual("n-loop-create-one", loop.BodyEntryNodeId, "the loop must execute the create node inline");

        var body = nodes.GetProperty(loop.BodyEntryNodeId!).GetProperty("config");
        Assert.IsFalse(
            body.GetProperty("body").GetProperty("item").TryGetProperty("Id", out _),
            "the created subtask must not carry the loop's per-iteration id while that id is a UUIDv5 (GR-17)");
        Assert.IsFalse(body.TryGetProperty("idempotencyKeyHeader", out _),
            "a loop-body node sends one generated key for every iteration, so the API would merge the subtasks");
    }

    /// <summary>Every node reachable from a loop node's body entry by edges (the body ends at a node with no edge).</summary>
    private static HashSet<string> LoopBodyNodeIds(string fileName)
    {
        using var document = JsonDocument.Parse(ReadWorkflowFile(fileName));
        var nodes = document.RootElement.GetProperty("nodes");
        var body = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<string>();
        foreach (var node in nodes.EnumerateObject())
            if (node.Value.GetProperty("type").GetString() == "loop"
                && node.Value.GetProperty("config").TryGetProperty("bodyEntryNodeId", out var entry))
                pending.Push(entry.GetString()!);

        while (pending.TryPop(out var id))
        {
            if (!body.Add(id) || !nodes.GetProperty(id).TryGetProperty("edges", out var edges)) continue;
            foreach (var edge in edges.EnumerateArray())
                pending.Push(edge.GetProperty("nextNodeId").GetString()!);
        }
        return body;
    }

    private static bool IsIntegrationNode(string fileName, string nodeId)
    {
        using var document = JsonDocument.Parse(ReadWorkflowFile(fileName));
        return document.RootElement.GetProperty("nodes").GetProperty(nodeId).GetProperty("type").GetString() == "integration";
    }

    /// <summary>Verifies read workflow file behavior and protects the expected test contract.</summary>
    private static string ReadWorkflowFile(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Workflows", fileName);
        return File.ReadAllText(path);
    }

    public TestContext TestContext { get; set; } = null!;
}
