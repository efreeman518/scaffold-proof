using EF.FlowEngine.Definition;
using EF.FlowEngine.Definition.NodeConfigs;
using EF.FlowEngine.Impl;
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
    /// package request 18 (docs/plans/ef-package-requests.md): EF.FlowEngine 1.0.163's "integration" node
    /// type (IntegrationNodeExecutor / IntegrationNodeConfig) has no way to set a per-node HTTP header,
    /// unlike the "fetch" node type (FetchNodeConfig.Headers, forwarded via BuildHeaders). ai-task-triage.json's
    /// PATCH nodes are "integration" nodes calling the TaskFlow API, which requires If-Match (428 without
    /// it) - so today those PATCH calls cannot satisfy the API's concurrency contract, and
    /// TriageWorkflow_AppliesSuggestedPriority_ThroughRealApi (Test.Integration) cannot pass until
    /// IntegrationNodeConfig gains a Headers property. This test documents the gap by construction: it
    /// fails (compile or assert) the moment a future EF.FlowEngine version adds Headers to
    /// IntegrationNodeConfig, which is the cue to wire it up here and in TaskItemTools /
    /// FunctionCategoryTrigger if similarly affected.
    /// </summary>
    [TestMethod]
    [TestCategory("Integration")]
    public void IntegrationNodeConfig_HasNoHeadersProperty_PackageRequest18()
    {
        var headersProperty = typeof(IntegrationNodeConfig).GetProperty("Headers");

        Assert.IsNull(headersProperty,
            "IntegrationNodeConfig now has a Headers property - package request 18 is resolved. " +
            "Wire ai-task-triage.json's PATCH nodes' \"headers\": {\"If-Match\": \"*\"} through " +
            "IntegrationNodeExecutor and drop this guard.");
    }

    /// <summary>
    /// The PATCH nodes already carry the forward-compatible "headers" config key (currently a silent
    /// no-op per IntegrationNodeConfig_HasNoHeadersProperty_PackageRequest18 above) so the fix in package
    /// request 18 goes live the moment it ships, with no further workflow JSON change needed.
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

    /// <summary>Verifies read workflow file behavior and protects the expected test contract.</summary>
    private static string ReadWorkflowFile(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Workflows", fileName);
        return File.ReadAllText(path);
    }

    public TestContext TestContext { get; set; } = null!;
}
