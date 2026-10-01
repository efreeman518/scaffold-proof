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

        using var document = JsonDocument.Parse(ReadWorkflowFile(fileName));
        var nodes = document.RootElement.GetProperty("nodes");

        // Package defect (EF.FlowEngine 1.0.197 and 1.0.199): GetWarnings looks the node's config key up as
        // PascalCase "IdempotencyKey", so a camelCase "idempotencyKey" in canonical workflow JSON is reported
        // as missing. Only that warning is excused, and only for a node whose config does carry a non-empty
        // key, so a genuinely missing key still fails. Remove this when GetWarnings reads the camelCase key.
        var warnings = WorkflowDefinitionValidator.GetWarnings(def)
            .Where(w => !IsCamelCaseIdempotencyKeyFalsePositive(w, nodes))
            .ToList();

        Assert.IsEmpty(warnings, $"{fileName}: {string.Join(" | ", warnings)}");
    }

    /// <summary>
    /// The node RetryPolicy is the only retry owner for the taskflow-api calls (the resilient HTTP adapter sends
    /// once per attempt), so every integration node declares one with exponential backoff. The POST nodes send the
    /// engine-generated key in the <c>Idempotency-Key</c> header the API deduplicates (D-074), which opts them into
    /// the full inherited status list (408, 409, 429, 500, 502, 503, 504) and transport retries. The If-Match: *
    /// PATCH nodes send no key and keep the engine's 409/429/503 unsafe-method default. No node overrides the
    /// inherited list, and no list carries 412 (D-032: a stale precondition is never resent).
    /// </summary>
    [TestMethod]
    [DynamicData(nameof(AllWorkflows))]
    [TestCategory("Integration")]
    public void Each_Integration_Node_Declares_Exponential_RetryPolicy_And_Only_Posts_Are_Keyed(string fileName, string _id, string _version)
    {
        var def = JsonSerializer.Deserialize<WorkflowDefinition>(ReadWorkflowFile(fileName), JsonOpts)!;
        var integrationNodes = def.Nodes.Values.Where(n => n.Type == "integration").ToList();
        Assert.IsNotEmpty(integrationNodes, $"{fileName} has no integration node");

        foreach (var node in integrationNodes)
        {
            var policy = node.RetryPolicy;
            Assert.IsNotNull(policy, $"{fileName}:{node.Id} must declare a retryPolicy");
            Assert.AreEqual(BackoffType.Exponential, policy.Backoff, $"{fileName}:{node.Id}");
            Assert.AreEqual(3, policy.MaxAttempts, $"{fileName}:{node.Id}");
            Assert.DoesNotContain(412, policy.RetryOnHttpStatus, $"{fileName}:{node.Id}");

            var config = JsonSerializer.Deserialize<IntegrationNodeConfig>(JsonSerializer.Serialize(node.Config, JsonOpts), JsonOpts)!;
            Assert.AreEqual("taskflow-api", config.ClientRef, $"{fileName}:{node.Id}");
            var expectedHeader = string.Equals(config.Method, "POST", StringComparison.OrdinalIgnoreCase) ? "Idempotency-Key" : null;
            Assert.AreEqual(expectedHeader, config.IdempotencyKeyHeader,
                $"{fileName}:{node.Id} ({config.Method}): only the POST nodes, whose API routes deduplicate the header, send it");
            Assert.IsNull(config.RetryOnStatusCodes, $"{fileName}:{node.Id} must inherit the default status list");
        }
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
    /// So the body sends no <c>Id</c>. Retry idempotency comes from the <c>Idempotency-Key</c> header instead
    /// (D-074): the engine sends one generated key across an iteration's attempts, and the API maps it to a stored
    /// UUIDv7, so every attempt creates or replays the same subtask. This test fails if someone re-adds the id
    /// or drops the header.
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
        Assert.AreEqual("Idempotency-Key", body.GetProperty("idempotencyKeyHeader").GetString(),
            "retry idempotency for the create must come from the deduplicated Idempotency-Key header instead");
    }

    private static bool IsCamelCaseIdempotencyKeyFalsePositive(string warning, JsonElement nodes)
    {
        var match = System.Text.RegularExpressions.Regex.Match(warning, @"^Node '(?<id>[^']+)' \([a-z]+\): IdempotencyKey is not set\.");
        return match.Success
            && nodes.TryGetProperty(match.Groups["id"].Value, out var node)
            && node.GetProperty("config").TryGetProperty("idempotencyKey", out var key)
            && !string.IsNullOrWhiteSpace(key.GetString());
    }

    /// <summary>Verifies read workflow file behavior and protects the expected test contract.</summary>
    private static string ReadWorkflowFile(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Workflows", fileName);
        return File.ReadAllText(path);
    }

    public TestContext TestContext { get; set; } = null!;
}
