using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Definition;
using EF.FlowEngine.Model;
using EF.Data.Contracts;
using EF.Storage.Contracts;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.Repositories;
using Test.Support.Builders;
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
public sealed partial class AiWorkflowIntegrationTests
{
    // Scaffold auth identity (ScaffoldPrincipal, EF.Auth fixed principal) the in-process host authenticates every request as.
    private const string TenantId = "00000000-0000-0000-0000-000000000001";
    private static readonly TimeSpan PollTimeout = TimeSpan.FromSeconds(60);
    // The engine drives the workflow in the background and terminates within a second or two. A short cadence
    // notices the terminal node promptly; FlowEngineWorkflowApiFactory raises the tenant budget so polling never trips it.
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(200);

    private static readonly string[] TerminalNodes =
        ["n-output-ok", "n-output-rejected", "n-output-failed", "n-faulted", "n-done", "n-failed"];

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

    /// <summary>
    /// D-074 through a loop body: the shipped decomposer creates one subtask per iteration with the engine's
    /// per-iteration Idempotency-Key. Each iteration's first POST reaches the API and creates the subtask, but the
    /// response is lost and the engine sees a 502; the node retryPolicy resends it with the same key and the API
    /// replays the stored row. Three iterations send three keys and leave three distinct subtasks.
    /// </summary>
    [TestMethod]
    public async Task DecomposerLoopBodyPost_RetriedOn502_CreatesOneSubtaskPerIteration()
    {
        SkipIfNoSql();
        var ct = TestContext.CancellationToken;
        var connectionString = await IsolatedMigratedConnectionStringAsync(ct);
        var lostResponses = new LostCreateResponses();

        using var factory = new FlowEngineWorkflowApiFactory(
            connectionString,
            _ => """{"subtasks":[{"title":"Sub A","estimateHours":1},{"title":"Sub B","estimateHours":2},{"title":"Sub C","estimateHours":3}]}""",
            selfCall => selfCall.AddHttpMessageHandler(() => new LoseFirstCreateResponsePerKey(lostResponses)));
        using var client = factory.CreateClient();

        var parentId = await CreateTaskAsync(client, "Keyed loop-body retry task", priority: 2 /* Medium */, ct);
        var instanceId = await StartWorkflowAsync(client, "ai-task-decomposer", new Dictionary<string, object?>
        {
            ["tenantId"] = TenantId,
            ["taskId"] = parentId.ToString(),
            ["description"] = "Build a login page with validation and password reset."
        }, ct);

        var (node, body) = await WaitForTerminalAsync(client, instanceId, ct);

        Assert.AreEqual("n-output-ok", node, $"Each 502 must be retried, not fail the loop. Instance: {Truncate(body)}");
        var keys = lostResponses.Keys.ToArray();
        Assert.HasCount(6, keys, "each iteration: one lost attempt plus one resend");
        Assert.HasCount(3, keys.Distinct(StringComparer.Ordinal).ToList(), "each iteration sends its own key");
        CollectionAssert.AreEqual(new[] { "Sub A", "Sub B", "Sub C" }, await ChildTitlesAsync(client, parentId, ct),
            "each resend must replay its iteration's subtask, and each iteration must create its own");
    }

    private sealed class LostCreateResponses
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<string> Keys = new();
    }

    // Forwards each keyed task create to the API (which commits it); on the first request carrying a key it answers
    // the engine with a 502 as if the response had been lost. State is shared because the client factory may build
    // more than one handler chain.
    private sealed class LoseFirstCreateResponsePerKey(LostCreateResponses state) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var isCreate = request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath.TrimEnd('/') == "/api/v1/task-items";
            if (!isCreate) return await base.SendAsync(request, cancellationToken);

            var key = request.Headers.TryGetValues("Idempotency-Key", out var values) ? values.Single() : null;
            Assert.IsFalse(string.IsNullOrWhiteSpace(key), "the loop-body create must send the generated key");
            var firstAttempt = !state.Keys.Contains(key);
            state.Keys.Enqueue(key!);
            var response = await base.SendAsync(request, cancellationToken);
            if (!firstAttempt) return response;

            Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, "the lost attempt must have created the subtask");
            response.Dispose();
            return new HttpResponseMessage(HttpStatusCode.BadGateway) { RequestMessage = request };
        }
    }

    private static async Task<string[]> ChildTitlesAsync(HttpClient client, Guid parentId, CancellationToken ct)
    {
        using var response = await client.PostAsJsonAsync(
            "/api/v1/task-items/search",
            new { pageSize = 50, filter = new { parentTaskItemId = parentId.ToString() } },
            ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"Search failed: {Truncate(body)}");
        using var payload = JsonDocument.Parse(body);
        return payload.RootElement.GetProperty("items").EnumerateArray()
            .Select(item => item.GetProperty("title").GetString()!)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// compliance-check end to end on the shipped definitions over the attachment-backed document store (D-075) and the lane's
    /// real object storage. Tenant A has three tasks due soon: one tagged "Compliance" with two uploaded text attachments and a
    /// newer PDF, one
    /// untagged with an uploaded attachment, and one tagged without an attachment; tenant B has a tagged task with its own
    /// attachment. The task search is bound to the started tenant (filter tenantId and the API's tenant query filter) and to
    /// the "compliance" tag name (case-insensitive), so the loop visits A's two tagged tasks; each child finds its task's
    /// newest text attachment through the attachment search and reads it through the store. The tagged task with evidence is
    /// classified expiring soon from its newest text attachment, not the PDF, and gets one reminder comment; the tagged task without an
    /// attachment takes the no-finding path with no document read; the untagged task and tenant B's task are never read and
    /// get no comment. The parent is started for tenant A as a trigger starts it, and the store serves evidence only to an
    /// instance of the attachment's tenant, so the one read also shows that each loop child inherits the parent's tenant.
    /// </summary>
    [TestMethod]
    public async Task ComplianceCheck_ScansOnlyTheStartedTenant_AndRemindsTheTaskWithExpiringEvidence()
    {
        SkipIfNoSql();
        var ct = TestContext.CancellationToken;
        var connectionString = await IsolatedMigratedConnectionStringAsync(ct);
        var dueDate = DateTimeOffset.UtcNow.AddDays(1);
        var tenantA = Guid.Parse(TenantId);
        var tenantB = Guid.CreateVersion7();
        var complianceA = new TagBuilder().WithTenantId(tenantA).WithName("Compliance").Build();
        var complianceB = new TagBuilder().WithTenantId(tenantB).WithName("compliance").Build();
        var taggedWithEvidence = DueTask(tenantA, "Compliance A tagged with evidence", dueDate, complianceA);
        var untaggedWithEvidence = DueTask(tenantA, "Compliance A untagged with evidence", dueDate);
        var taggedWithoutEvidence = DueTask(tenantA, "Compliance A tagged without evidence", dueDate, complianceA);
        var otherTenant = DueTask(tenantB, "Compliance B tagged with evidence", dueDate, complianceB);
        var otherTenantEvidence = TextAttachment(tenantB, otherTenant.Id.Value, "evidence-b.txt", "certificate expires next week");
        await using (var seed = DbContainerFixture.CreateTrxnContext(connectionString))
        {
            seed.Tags.AddRange(complianceA, complianceB);
            seed.TaskItems.AddRange(taggedWithEvidence, untaggedWithEvidence, taggedWithoutEvidence, otherTenant);
            seed.Attachments.Add(otherTenantEvidence.Row);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);
        }

        var reads = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var prompts = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var factory = new FlowEngineWorkflowApiFactory(
            connectionString,
            prompt =>
            {
                prompts.Enqueue(prompt);
                return """{"status":"expiringSoon","summary":"expires next week"}""";
            },
            configureServices: services => RecordDocumentReads(services, reads));
        using var client = factory.CreateClient();
        await StoreBlobAsync(factory, otherTenantEvidence, ct);
        var olderEvidence = await UploadAttachmentAsync(client, taggedWithEvidence.Id.Value, "evidence-2025.txt", "text/plain",
            "certificate renewed, valid for two years", ct);
        var latestEvidence = await UploadAttachmentAsync(client, taggedWithEvidence.Id.Value, "evidence-2026.md", "text/markdown",
            "certificate expires next week", ct);
        // The newest attachment is binary; the search asks for text/plain and text/markdown, so the text above is still read.
        await UploadAttachmentAsync(client, taggedWithEvidence.Id.Value, "scan-2026.pdf", "application/pdf", "%PDF-1.7 binary", ct);
        await UploadAttachmentAsync(client, untaggedWithEvidence.Id.Value, "evidence.txt", "text/plain",
            "certificate expires next week", ct);

        var instanceId = await StartForTenantAsync(factory, "compliance-check", new Dictionary<string, object?>
        {
            ["tenantId"] = TenantId,
            ["dueBefore"] = DateTimeOffset.UtcNow.AddDays(7).ToString("O")
        }, ct);
        var (node, body) = await WaitForTerminalAsync(client, instanceId, ct);
        var children = await ChildInstancesAsync(client, "compliance-check-item", ct);
        var diagnostics = $"Reads: [{string.Join(", ", reads)}]; newest text {latestEvidence}, older text {olderEvidence}; "
            + $"prompts: {prompts.Count}; reminder comments: {await CountCommentsAsync(client, taggedWithEvidence.Id.Value, ct)}. "
            + $"Parents: {Describe(await ChildInstancesAsync(client, "compliance-check", ct))}. "
            + $"Children: {Describe(children)}.";
        TestContext.WriteLine(diagnostics);

        Assert.AreEqual("n-output-ok", node, diagnostics);
        // Exactly one child per tagged task of tenant A, each ending on n-done: a failed search on the child without
        // evidence ends it on n-failed, and an untagged or tenant B task in the loop adds a child.
        CollectionAssert.AreEquivalent(
            new[] { "Compliance A tagged with evidence -> n-done", "Compliance A tagged without evidence -> n-done" },
            children.Select(c => $"{c.Item} -> {c.At}").ToArray(),
            "the loop starts one child per tagged task of the started tenant, and each ends on n-done. " + diagnostics);
        // The parent waits on its children and is resumed by their completion signals. A lease is exclusive, even for the
        // claimant that holds it, so a resume that met a lease still held would be skipped and the sweep would pick the
        // parent up only after the 30 s lease expired.
        Assert.IsLessThan(WellInsideOneLease, ElapsedSinceCreated(body), "the child-signal resumes finish well inside one lease. " + diagnostics);
        Assert.AreNotEqual(olderEvidence, latestEvidence);
        CollectionAssert.AreEqual(new[] { latestEvidence.ToString() }, reads.ToArray(),
            "only the tagged task with evidence is read, by its newest attachment id; the untagged task and tenant B are never read. " + diagnostics);
        var prompt = prompts.Single();
        StringAssert.Contains(prompt, "Compliance A tagged with evidence", "the prompt binds params.currentItem.title");
        StringAssert.Contains(prompt, "certificate expires next week", "the prompt carries the newest attachment's text");
        Assert.DoesNotContain("valid for two years", prompt, "an older attachment is not the evidence");
        Assert.AreEqual(1, await CountCommentsAsync(client, taggedWithEvidence.Id.Value, ct), "the expiring task gets one reminder");
        Assert.AreEqual(0, await CountCommentsAsync(client, taggedWithoutEvidence.Id.Value, ct), "no attachment takes the no-finding path");
        Assert.AreEqual(0, await CountCommentsAsync(client, untaggedWithEvidence.Id.Value, ct), "an untagged task is not processed");
        Assert.AreEqual(0, await CountCommentsIgnoringTenantAsync(connectionString, otherTenant.Id, ct), "another tenant's task is never touched");
    }

    /// <summary>
    /// D-075: the attachment id a document node reads comes from the tenant-scoped attachment search. A compliance-check-item
    /// run started by tenant A for tenant B's task, with B's task id and tenant in <c>params.currentItem</c>, finds no
    /// attachment because the API's tenant query filter pins the search to the caller, so the store is never asked for B's
    /// attachment, the run ends on the no-finding path and B's task gets no comment. A run for tenant A's own task that names
    /// tenant B in <c>params.currentItem.tenantId</c> finds nothing either: the search filter's tenantId must match too, and it
    /// is the only scope for a tenant-less caller, so this case fails if the workflow stops sending it.
    /// </summary>
    [TestMethod]
    public async Task ComplianceCheckItem_ForAnotherTenantsTask_NeverReadsItsAttachment()
    {
        SkipIfNoSql();
        var ct = TestContext.CancellationToken;
        var connectionString = await IsolatedMigratedConnectionStringAsync(ct);
        var tenantB = Guid.CreateVersion7();
        var otherTenant = DueTask(tenantB, "Compliance B task", DateTimeOffset.UtcNow.AddDays(1));
        var otherTenantEvidence = TextAttachment(tenantB, otherTenant.Id.Value, "evidence-b.txt", "certificate expires next week");
        await using (var seed = DbContainerFixture.CreateTrxnContext(connectionString))
        {
            seed.TaskItems.Add(otherTenant);
            seed.Attachments.Add(otherTenantEvidence.Row);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);
        }

        var reads = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var factory = new FlowEngineWorkflowApiFactory(
            connectionString,
            _ => """{"status":"expiringSoon","summary":"expires next week"}""",
            configureServices: services => RecordDocumentReads(services, reads));
        using var client = factory.CreateClient();
        await StoreBlobAsync(factory, otherTenantEvidence, ct);

        var instanceId = await StartForTenantAsync(factory, "compliance-check-item", new Dictionary<string, object?>
        {
            ["currentItem"] = new { id = otherTenant.Id.Value, tenantId = tenantB, title = "Compliance B task" }
        }, ct);
        var (node, body) = await WaitForTerminalAsync(client, instanceId, ct);

        Assert.AreEqual("n-done", node, $"Instance: {Truncate(body)}");
        Assert.IsEmpty(reads, "another tenant's attachment id never reaches the document store");

        var ownTask = await CreateTaskAsync(client, "Compliance A task", priority: 2 /* Medium */, ct);
        await UploadAttachmentAsync(client, ownTask, "evidence.txt", "text/plain", "certificate expires next week", ct);
        var mislabelled = await StartForTenantAsync(factory, "compliance-check-item", new Dictionary<string, object?>
        {
            ["currentItem"] = new { id = ownTask, tenantId = tenantB, title = "Compliance A task" }
        }, ct);
        var (mislabelledNode, mislabelledBody) = await WaitForTerminalAsync(client, mislabelled, ct);
        Assert.AreEqual("n-done", mislabelledNode, $"Instance: {Truncate(mislabelledBody)}");
        Assert.IsEmpty(reads, "the search filter's tenantId scopes the search: a task labelled with another tenant is not read");
        Assert.AreEqual(0, await CountCommentsIgnoringTenantAsync(connectionString, otherTenant.Id, ct), "another tenant's task is never touched");
    }

    /// <summary>
    /// D-075, the admin start route on one host. Without <c>tenantId</c> the instance has no tenant: a compliance-check-item
    /// run for the scaffold tenant's own task with text evidence finds the attachment, and the store refuses the read (the
    /// run ends on n-failed with no agent call and no comment). With <c>tenantId</c> = the scaffold tenant, a compliance-check
    /// run carries that tenant, its child reads the due task's evidence and the task gets its reminder. The scaffold
    /// principal carries tenant <c>0001</c> and <c>GlobalAdmin</c>, but in Scaffold mode the Admin API reads its tenant from
    /// <c>flowengine_tenant_id</c>, which it does not carry, so it is a caller without a tenant and the package honours any
    /// <c>tenantId</c>: a start for another tenant runs in that tenant (a caller with a tenant would get 403).
    /// One host for the three starts keeps the test process within EF Core's internal service provider limit.
    /// </summary>
    [TestMethod]
    public async Task AdminStart_CarriesTheRequestedTenant_AndWithoutOneIsRefusedTheEvidence()
    {
        SkipIfNoSql();
        var ct = TestContext.CancellationToken;
        var connectionString = await IsolatedMigratedConnectionStringAsync(ct);
        var reads = new System.Collections.Concurrent.ConcurrentQueue<string>();
        var prompts = 0;
        using var factory = new FlowEngineWorkflowApiFactory(
            connectionString,
            _ =>
            {
                Interlocked.Increment(ref prompts);
                return """{"status":"expiringSoon","summary":"expires next week"}""";
            },
            configureServices: services => RecordDocumentReads(services, reads));
        using var client = factory.CreateClient();
        var task = await CreateTaskAsync(client, "Compliance task", priority: 2 /* Medium */, ct);
        var evidence = await UploadAttachmentAsync(client, task, "evidence.txt", "text/plain", "certificate expires next week", ct);

        var instanceId = await StartWorkflowAsync(client, "compliance-check-item", new Dictionary<string, object?>
        {
            ["currentItem"] = new { id = task, tenantId = TenantId, title = "Compliance task" }
        }, ct);
        var (node, body) = await WaitForTerminalAsync(client, instanceId, ct);

        Assert.AreEqual("n-failed", node, $"Instance: {Truncate(body)}");
        CollectionAssert.AreEqual(new[] { evidence.ToString() }, reads.ToArray(), "the search found the evidence and the read was attempted");
        StringAssert.Contains(body, "no tenant", "the store's refusal is the instance error");
        Assert.AreEqual(0, prompts, "no evidence reaches the agent");
        Assert.AreEqual(0, await CountCommentsAsync(client, task, ct), "the task gets no comment");

        // With tenantId = the scaffold tenant: compliance-check over a due compliance task with evidence.
        var now = DateTimeOffset.UtcNow;
        var tenant = Guid.Parse(TenantId);
        var compliance = new TagBuilder().WithTenantId(tenant).WithName("Compliance").Build();
        var due = DueTask(tenant, "Admin start with evidence", now.AddDays(1), compliance);
        await using (var seed = DbContainerFixture.CreateTrxnContext(connectionString))
        {
            seed.Tags.Add(compliance);
            seed.TaskItems.Add(due);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);
        }
        var dueEvidence = await UploadAttachmentAsync(client, due.Id.Value, "evidence.txt", "text/plain", "certificate expires next week", ct);
        var store = factory.Services.GetRequiredService<IExecutionStateStore>();

        var tenantInstanceId = await StartWorkflowAsync(client, "compliance-check", ComplianceCheckParams(TenantId, now), ct, tenantId: TenantId);
        var (tenantNode, tenantBody) = await WaitForTerminalAsync(client, tenantInstanceId, ct);
        var children = await ChildInstancesAsync(client, "compliance-check-item", ct);
        var diagnostics = $"Reads: [{string.Join(", ", reads)}]; evidence {dueEvidence}; children: {Describe(children)}. Parent: {Truncate(tenantBody)}";

        Assert.AreEqual(TenantId, (await store.LoadAsync(tenantInstanceId, ct))?.TenantId, "the admin start carries the requested tenant. " + diagnostics);
        Assert.AreEqual("n-output-ok", tenantNode, diagnostics);
        CollectionAssert.Contains(children.Select(c => $"{c.Item} -> {c.At}").ToArray(), "Admin start with evidence -> n-done", diagnostics);
        CollectionAssert.AreEqual(new[] { evidence.ToString(), dueEvidence.ToString() }, reads.ToArray(),
            "the child reads the due task's evidence as the instance tenant. " + diagnostics);
        Assert.AreEqual(1, await CountCommentsAsync(client, due.Id.Value, ct), "the expiring task gets one reminder. " + diagnostics);

        // With tenantId = another tenant, from the same caller.
        var other = Guid.CreateVersion7().ToString();
        var otherInstanceId = await StartWorkflowAsync(client, "compliance-check", ComplianceCheckParams(other, now), ct, tenantId: other);
        var (_, otherBody) = await WaitForTerminalAsync(client, otherInstanceId, ct);
        Assert.AreEqual(other, (await store.LoadAsync(otherInstanceId, ct))?.TenantId,
            "a caller without an Admin API tenant starts in the requested tenant. " + Truncate(otherBody));
    }

    /// <summary>
    /// D-075 store contract on the lane's real object storage (S3 on NonAzure, Azure Blob on Azure), through the store the
    /// host registers: a text attachment uploaded through the API reads back as its text, also after a rename (the read
    /// uses the stored object key, not the file name); a binary attachment, one larger
    /// than <see cref="AttachmentDocumentStore.MaxEvidenceBytes"/>, an unknown attachment id and a reference that is not an
    /// id are refused, the oversized one also when its row understates its size (the bounded copy); writing is not
    /// supported. Every call carries the scaffold tenant that uploaded the attachments.
    /// </summary>
    [TestMethod]
    public async Task AttachmentDocumentStore_ReadsTextEvidence_AndRefusesBinaryOversizedUnknownOrWrites()
    {
        SkipIfNoSql();
        var ct = TestContext.CancellationToken;
        var connectionString = await IsolatedMigratedConnectionStringAsync(ct);
        using var factory = new FlowEngineWorkflowApiFactory(connectionString, _ => "{}");
        using var client = factory.CreateClient();
        var taskId = await CreateTaskAsync(client, "Evidence store task", priority: 2 /* Medium */, ct);
        var text = await UploadAttachmentAsync(client, taskId, "evidence.txt", "text/plain; charset=utf-8", "certificate expires next week", ct);
        var binary = await UploadAttachmentAsync(client, taskId, "evidence.pdf", "application/pdf", "%PDF-1.7 binary", ct);
        var oversized = await UploadAttachmentAsync(client, taskId, "evidence-large.txt", "text/plain",
            new string('x', AttachmentDocumentStore.MaxEvidenceBytes + 1), ct);
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        await RenameAttachmentAsync(client, text, "renamed.txt", ct);

        Assert.IsInstanceOfType<AttachmentDocumentStore>(store, "the host registers the attachment-backed store");
        await using (var stream = await store.OpenReadAsync(text.ToString(), TenantId, ct))
        using (var reader = new StreamReader(stream))
        {
            Assert.AreEqual("certificate expires next week", await reader.ReadToEndAsync(ct));
        }
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() => store.OpenReadAsync(binary.ToString(), TenantId, ct));
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.OpenReadAsync(oversized.ToString(), TenantId, ct));
        // The API refuses a size change on uploaded content (D-075); a row that understates its size anyway (written
        // outside the API) still meets the bounded copy of the blob, which refuses it.
        await using (var db = DbContainerFixture.CreateTrxnContext(connectionString))
        {
            var oversizedId = TaskFlow.Domain.Shared.AttachmentId.From(oversized);
            Assert.AreEqual(1, await db.Attachments.IgnoreQueryFilters().Where(a => a.Id == oversizedId)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.FileSizeBytes, 1L), ct));
        }
        var copyRefusal = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => store.OpenReadAsync(oversized.ToString(), TenantId, ct));
        StringAssert.Contains(copyRefusal.Message, "more than", "the copy, not the row size check, refused it");
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => store.OpenReadAsync(Guid.CreateVersion7().ToString(), TenantId, ct));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.OpenReadAsync("not-an-attachment-id", TenantId, ct));
        await Assert.ThrowsExactlyAsync<NotSupportedException>(() =>
            store.StoreAsync(new MemoryStream([1]), "evidence.txt", "text/plain", TenantId, ct));
    }

    /// <summary>
    /// D-075: the store itself enforces the tenant, whatever reference a workflow hands it. Tenant B's text attachment
    /// reads back for an instance of tenant B, and is refused for an instance of tenant A, and for an instance with no
    /// tenant, before any content is served.
    /// </summary>
    [TestMethod]
    public async Task AttachmentDocumentStore_RefusesAnotherTenantsAttachment_AndAnInstanceWithNoTenant()
    {
        SkipIfNoSql();
        var ct = TestContext.CancellationToken;
        var connectionString = await IsolatedMigratedConnectionStringAsync(ct);
        var tenantB = Guid.CreateVersion7();
        var taskB = DueTask(tenantB, "Compliance B task", DateTimeOffset.UtcNow.AddDays(1));
        var evidenceB = TextAttachment(tenantB, taskB.Id.Value, "evidence-b.txt", "certificate expires next week");
        await using (var seed = DbContainerFixture.CreateTrxnContext(connectionString))
        {
            seed.TaskItems.Add(taskB);
            seed.Attachments.Add(evidenceB.Row);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);
        }

        using var factory = new FlowEngineWorkflowApiFactory(connectionString, _ => "{}");
        using var client = factory.CreateClient();
        await StoreBlobAsync(factory, evidenceB, ct);
        var store = factory.Services.GetRequiredService<IDocumentStore>();
        var reference = evidenceB.Row.Id.Value.ToString();

        await using (var stream = await store.OpenReadAsync(reference, tenantB.ToString(), ct))
        using (var reader = new StreamReader(stream))
        {
            Assert.AreEqual("certificate expires next week", await reader.ReadToEndAsync(ct), "the owning tenant reads it");
        }
        var foreign = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.OpenReadAsync(reference, TenantId, ct));
        StringAssert.Contains(foreign.Message, "another tenant");
        var tenantless = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => store.OpenReadAsync(reference, null, ct));
        StringAssert.Contains(tenantless.Message, "no tenant");
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => store.OpenReadAsync(reference, "not-a-tenant-id", ct));
    }

    // One row per instance of the workflow: its item title, its current node, and a line with id, status, terminal node,
    // the nodes it visited and any error, so a failed assertion shows which child took which path.
    private sealed record InstanceRow(string? Item, string? At, string Line);

    private static string Describe(IReadOnlyList<InstanceRow> rows) => string.Join(" | ", rows.Select(r => r.Line));

    private static async Task<IReadOnlyList<InstanceRow>> ChildInstancesAsync(HttpClient client, string workflowId, CancellationToken ct)
    {
        using var response = await client.GetAsync($"/api/flowengine/instances?workflowId={workflowId}&take=50", ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        Assert.IsTrue(response.IsSuccessStatusCode, $"{workflowId} instance list failed: {(int)response.StatusCode} {Truncate(text)}");
        using var json = JsonDocument.Parse(text);
        var items = json.RootElement.ValueKind == JsonValueKind.Array
            ? json.RootElement
            : json.RootElement.EnumerateObject().First(p => p.Value.ValueKind == JsonValueKind.Array).Value;
        return items.EnumerateArray().Select(i =>
        {
            string? Field(string name) => i.EnumerateObject()
                .FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)).Value is { ValueKind: not JsonValueKind.Undefined } v
                ? v.ToString() : null;
            var history = i.EnumerateObject().FirstOrDefault(p => string.Equals(p.Name, "history", StringComparison.OrdinalIgnoreCase)).Value;
            var visited = history.ValueKind == JsonValueKind.Array
                ? string.Join(">", history.EnumerateArray().Select(h => FindStringProperty(h, "nodeId") + ":" + FindStringProperty(h, "outcome")))
                : "?";
            var title = i.TryGetProperty("context", out var context) ? FindStringProperty(context, "title") : null;
            return new InstanceRow(title, Field("currentNodeId"),
                $"{Field("instanceId")} created={Field("createdAt")} parentNode={Field("parentNodeId")} item={title} status={Field("status")} "
                + $"at={Field("currentNodeId")} visits={Field("nodeVisitCounts")} path={visited} error={Field("error")}");
        }).ToList();
    }

    private static TaskFlow.Domain.Model.TaskItem DueTask(
        Guid tenantId, string title, DateTimeOffset dueDate, params TaskFlow.Domain.Model.Tag[] tags)
    {
        var task = new TaskItemBuilder().WithTenantId(tenantId).WithTitle(title).Build();
        task.UpdateDateRange(null, dueDate);
        foreach (var tag in tags) task.AssociateTag(tag.Id);
        return task;
    }

    private static async Task<int> CountCommentsIgnoringTenantAsync(
        string connectionString, TaskFlow.Domain.Shared.TaskItemId taskId, CancellationToken ct)
    {
        await using var verify = DbContainerFixture.CreateTrxnContext(connectionString);
        return await verify.TaskItems.IgnoreQueryFilters()
            .Where(t => t.Id == taskId)
            .SelectMany(t => t.Comments)
            .CountAsync(ct);
    }

    private sealed record SeededAttachment(TaskFlow.Domain.Model.Attachment Row, byte[] Content);

    // An attachment row as the upload path writes it, for a tenant the scaffold principal cannot upload as.
    private static SeededAttachment TextAttachment(Guid tenantId, Guid taskId, string fileName, string text)
    {
        var content = System.Text.Encoding.UTF8.GetBytes(text);
        var row = new AttachmentBuilder()
            .WithTenantId(tenantId)
            .WithOwnerType(AttachmentOwnerType.TaskItem)
            .WithOwnerId(taskId)
            .WithFileName(fileName)
            .WithContentType("text/plain")
            .WithFileSizeBytes(content.Length)
            .WithStorageUri($"seeded:{fileName}")
            .WithStorageKey(AttachmentBlobs.NewObjectKey(tenantId, taskId, fileName))
            .Build();
        return new SeededAttachment(row, content);
    }

    // Writes a seeded attachment's blob where the upload path would, once the host has provisioned the container.
    private static async Task StoreBlobAsync(FlowEngineWorkflowApiFactory factory, SeededAttachment attachment, CancellationToken ct)
    {
        await using var scope = factory.Services.CreateAsyncScope();
        var blobs = scope.ServiceProvider.GetRequiredService<IObjectStorageRepository>();
        using var content = new MemoryStream(attachment.Content);
        await blobs.UploadAsync(
            AttachmentBlobs.ContainerName,
            attachment.Row.StorageKey!,
            content, attachment.Row.ContentType, cancellationToken: ct);
    }

    private static async Task<Guid> UploadAttachmentAsync(
        HttpClient client, Guid taskId, string fileName, string contentType, string text, CancellationToken ct)
    {
        using var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(System.Text.Encoding.UTF8.GetBytes(text));
        file.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        content.Add(file, "file", fileName);
        content.Add(new StringContent(((int)AttachmentOwnerType.TaskItem).ToString(System.Globalization.CultureInfo.InvariantCulture)), "ownerType");
        content.Add(new StringContent(taskId.ToString()), "ownerId");
        using var response = await client.PostAsync("/api/v1/attachments/upload", content, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.AreEqual(HttpStatusCode.Created, response.StatusCode, $"Upload failed: {Truncate(body)}");
        using var payload = JsonDocument.Parse(body);
        return payload.RootElement.GetProperty("item").GetProperty("id").GetGuid();
    }

    private static Task RenameAttachmentAsync(HttpClient client, Guid attachmentId, string fileName, CancellationToken ct) =>
        PutAttachmentFieldAsync(client, attachmentId, "fileName", fileName, ct);

    // PUTs the attachment's current item with one field replaced.
    private static async Task PutAttachmentFieldAsync(HttpClient client, Guid attachmentId, string field, JsonNode value, CancellationToken ct)
    {
        var current = await client.GetFromJsonAsync<JsonObject>($"/api/v1/attachments/{attachmentId}", ct);
        var item = current!["item"]!.AsObject();
        item[field] = value;
        using var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/attachments/{attachmentId}")
        {
            Content = JsonContent.Create(new JsonObject { ["item"] = item.DeepClone() })
        };
        request.Headers.TryAddWithoutValidation("If-Match", "*");
        using var response = await client.SendAsync(request, ct);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"PUT {field} failed: {Truncate(await response.Content.ReadAsStringAsync(ct))}");
    }

    // Keeps the host's own IDocumentStore registration (asserted to be the attachment-backed store) and records each
    // store reference a document node reads before the real store serves it.
    private static void RecordDocumentReads(IServiceCollection services, System.Collections.Concurrent.ConcurrentQueue<string> reads)
    {
        var registration = services.Single(d => d.ServiceType == typeof(IDocumentStore));
        Assert.AreEqual(typeof(AttachmentDocumentStore), registration.ImplementationType, "the host registers the attachment-backed store");
        services.Remove(registration);
        services.AddSingleton<IDocumentStore>(sp => new RecordingDocumentStore(ActivatorUtilities.CreateInstance<AttachmentDocumentStore>(sp), reads));
    }

    private sealed class RecordingDocumentStore(IDocumentStore inner, System.Collections.Concurrent.ConcurrentQueue<string> reads) : IDocumentStore
    {
        public Task<Stream> OpenReadAsync(string storeRef, string? tenantId, CancellationToken ct = default)
        {
            reads.Enqueue(storeRef);
            return inner.OpenReadAsync(storeRef, tenantId, ct);
        }

        public Task<DocumentContextValue> StoreAsync(Stream content, string fileName, string contentType, string? tenantId, CancellationToken ct = default) =>
            inner.StoreAsync(content, fileName, contentType, tenantId, ct);
    }

    // The FlowEngine lease is 30 s (AddFlowEngineServices); a resume skipped because a lease was still held waits it out.
    private static readonly TimeSpan WellInsideOneLease = TimeSpan.FromSeconds(15);
    private const string SuspendResumeProbeId = "suspend-resume-probe";

    /// <summary>
    /// An instance that suspends clears its claim and releases its lease before anything resumes it. The lock provider
    /// refuses every acquire while a lease is unexpired, the holder's own included, so a suspension that kept its lease
    /// would make each resume be skipped until the sweep took the instance after the 30 s lease. A probe workflow suspends at a human node, is resumed by the task response, suspends at a wait
    /// node, is resumed through the admin resume route, and completes. At each suspension the instance carries no
    /// claim, and each resume reaches the next node well inside one 30 s lease.
    /// </summary>
    [TestMethod]
    public async Task SuspendedInstance_ResumedByHumanResponseAndWaitSignal_CompletesWithoutWaitingOutALease()
    {
        SkipIfNoSql();
        var ct = TestContext.CancellationToken;
        var connectionString = await IsolatedMigratedConnectionStringAsync(ct);
        using var factory = new FlowEngineWorkflowApiFactory(connectionString, _ => "{}");
        using var client = factory.CreateClient();
        await factory.Services.GetRequiredService<IWorkflowRegistry>().SaveAsync(SuspendResumeProbe(), ct);

        var instanceId = await StartWorkflowAsync(client, SuspendResumeProbeId, new Dictionary<string, object?> { ["key"] = "probe" }, ct);
        var atHuman = await WaitForSuspendedAtAsync(client, instanceId, "n-approve", ct);
        AssertNoClaim(atHuman);

        using var tasks = await client.GetAsync($"/api/flowengine/human-tasks/instance/{instanceId}", ct);
        var tasksBody = await tasks.Content.ReadAsStringAsync(ct);
        Assert.AreEqual(HttpStatusCode.OK, tasks.StatusCode, Truncate(tasksBody));
        var taskId = FindStringProperty(JsonDocument.Parse(tasksBody).RootElement, "taskId");
        var responded = System.Diagnostics.Stopwatch.StartNew();
        using (var respond = await client.PostAsJsonAsync($"/api/flowengine/human-tasks/{taskId}/respond",
                   new { formValues = new { decision = "approve" }, respondedBy = "scaffold-user" }, ct))
        {
            Assert.IsTrue(respond.IsSuccessStatusCode, $"Respond failed: {(int)respond.StatusCode} {Truncate(await respond.Content.ReadAsStringAsync(ct))}");
        }
        var atWait = await WaitForSuspendedAtAsync(client, instanceId, "n-wait", ct);
        Assert.IsLessThan(WellInsideOneLease, responded.Elapsed, "the human response resumes the instance well inside one lease");
        AssertNoClaim(atWait);

        var signalled = System.Diagnostics.Stopwatch.StartNew();
        using (var resume = await client.PostAsJsonAsync($"/api/flowengine/instances/{instanceId}/resume", new { payload = new { go = true } }, ct))
        {
            Assert.IsTrue(resume.IsSuccessStatusCode, $"Resume failed: {(int)resume.StatusCode} {Truncate(await resume.Content.ReadAsStringAsync(ct))}");
        }
        var (node, body) = await WaitForTerminalAsync(client, instanceId, ct);

        Assert.AreEqual("n-output-ok", node, Truncate(body));
        Assert.IsLessThan(WellInsideOneLease, signalled.Elapsed, "the wait signal resumes the instance well inside one lease");
    }

    // Polls quickly (the measured resumes take well under a second) until the instance is suspended at the node.
    private static async Task<JsonElement> WaitForSuspendedAtAsync(HttpClient client, string instanceId, string nodeId, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow.Add(PollTimeout);
        var last = string.Empty;
        while (DateTimeOffset.UtcNow < deadline)
        {
            using var response = await client.GetAsync($"/api/flowengine/instances/{instanceId}", ct);
            last = await response.Content.ReadAsStringAsync(ct);
            Assert.IsTrue(response.IsSuccessStatusCode, $"Instance read failed: {(int)response.StatusCode}. {Truncate(last)}");
            var root = JsonDocument.Parse(last).RootElement;
            if (FindStringProperty(root, "currentNodeId") == nodeId && FindStringProperty(root, "status") == "Suspended")
                return root.Clone();
            await Task.Delay(TimeSpan.FromMilliseconds(200), ct);
        }

        Assert.Fail($"Timed out waiting for {nodeId}. Last instance body: {Truncate(last)}");
        throw new InvalidOperationException("Unreachable");
    }

    // The instance body carries the claim fields; a suspended instance has them cleared.
    private static void AssertNoClaim(JsonElement instance) =>
        Assert.IsTrue(instance.TryGetProperty("claimedBy", out var claim) && claim.ValueKind == JsonValueKind.Null,
            $"a suspended instance holds no claim: {Truncate(instance.GetRawText())}");

    private static TimeSpan ElapsedSinceCreated(string instanceBody)
    {
        var root = JsonDocument.Parse(instanceBody).RootElement;
        return root.GetProperty("completedAt").GetDateTimeOffset() - root.GetProperty("createdAt").GetDateTimeOffset();
    }

    private static WorkflowDefinition SuspendResumeProbe() => WorkflowDefinitionBuilder.FromJson("""
        {
          "id": "suspend-resume-probe",
          "version": "1.0.0",
          "status": "Active",
          "entryNodeId": "n-approve",
          "nodes": {
            "n-approve": {
              "id": "n-approve", "type": "human",
              "config": {
                "title": "Approve the probe", "assignedTo": "role:probe", "channel": "Internal", "quorum": 1,
                "formSchema": "{\"properties\":{\"decision\":{\"type\":\"string\"}},\"required\":[\"decision\"]}"
              },
              "edges": [ { "on": ["Match"], "nextNodeId": "n-wait" } ]
            },
            "n-wait": {
              "id": "n-wait", "type": "wait",
              "config": { "eventName": "probe-go", "correlationKeyPath": "$.params.key" },
              "edges": [ { "on": ["Match"], "nextNodeId": "n-output-ok" } ]
            },
            "n-output-ok": { "id": "n-output-ok", "type": "output", "config": {} }
          }
        }
        """).Build();

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

    // Starts the workflow through the admin start route. With tenantId the request carries the instance tenant, which
    // the package honours for a caller that resolves to no Admin API tenant (the scaffold principal in Scaffold mode).
    private static async Task<string> StartWorkflowAsync(
        HttpClient client, string workflowId, Dictionary<string, object?> parameters, CancellationToken ct, string? tenantId = null)
    {
        using var response = await PostAdminStartAsync(client, workflowId, parameters, tenantId, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        Assert.IsTrue(
            response.StatusCode is HttpStatusCode.OK or HttpStatusCode.Accepted or HttpStatusCode.Created,
            $"Workflow start failed: {(int)response.StatusCode}. {Truncate(body)}");
        using var payload = JsonDocument.Parse(body);
        var instanceId = FindStringProperty(payload.RootElement, "instanceId");
        Assert.IsFalse(string.IsNullOrWhiteSpace(instanceId), $"No instanceId in start response: {Truncate(body)}");
        return instanceId!;
    }

    private static Task<HttpResponseMessage> PostAdminStartAsync(
        HttpClient client, string workflowId, Dictionary<string, object?> parameters, string? tenantId, CancellationToken ct)
    {
        var request = new Dictionary<string, object?>
        {
            ["workflowId"] = workflowId,
            ["correlationId"] = Guid.NewGuid().ToString("N"),
            ["params"] = parameters
        };
        if (tenantId is not null)
            request["tenantId"] = tenantId;
        return client.PostAsJsonAsync("/api/flowengine/instances/start", request, ct);
    }

    // Starts the workflow as a trigger does (WorkflowTriggerHandler): through IFlowEngine with the instance tenant set, so
    // the instance and its loop children read evidence as that tenant. An admin start without tenantId has no tenant, and
    // the attachment-backed store refuses its evidence reads.
    private static async Task<string> StartForTenantAsync(
        FlowEngineWorkflowApiFactory factory, string workflowId, Dictionary<string, object?> parameters, CancellationToken ct)
    {
        var instance = await factory.Services.GetRequiredService<IFlowEngine>().StartBackgroundAsync(new StartRequest
        {
            WorkflowId = workflowId,
            TenantId = TenantId,
            CorrelationId = Guid.NewGuid().ToString("N"),
            Params = parameters.ToDictionary(
                p => p.Key, ContextValue (p) => new JsonContextValue { Value = JsonSerializer.SerializeToElement(p.Value) }),
        }, ct);
        return instance.InstanceId;
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
