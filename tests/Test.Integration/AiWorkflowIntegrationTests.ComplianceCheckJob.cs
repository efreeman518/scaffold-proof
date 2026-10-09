extern alias SchedulerHost;

using EF.Data.Contracts;
using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedulerHost::TaskFlow.Scheduler.Handlers;
using System.Text.Json;
using TaskFlow.Bootstrapper;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support.Builders;

namespace Test.Integration;

public sealed partial class AiWorkflowIntegrationTests
{
    /// <summary>
    /// The start path of compliance-check (D-075) without the self-call relay (Scaffold mode): the Scheduler's ComplianceCheck
    /// job starts the workflow only for the tenant its API calls act for, the scaffold tenant, and that instance scans the
    /// tenant's due compliance tasks and reads the evidence (refused to an instance without a tenant, see
    /// <see cref="ComplianceCheckItem_StartedWithoutATenant_IsRefusedItsEvidence"/>). Another tenant with a due task
    /// tagged " COMPLIANCE " qualifies but gets no instance: the API calls would read the scaffold tenant's tasks, not its
    /// own, and report it swept. With the relay configured it starts
    /// (<see cref="ComplianceCheckJob_StartsOneInstancePerQualifyingTenant_CarryingThatTenant"/>). A re-run the same UTC day, after the first instance has started its children, resolves
    /// to the day's instance instead of starting a second.
    /// </summary>
    [TestMethod]
    public async Task ComplianceCheckJob_StartsTheScaffoldTenantOnly_AndItsInstanceScansItsDueTasks()
    {
        SkipIfNoSql();
        var ct = TestContext.CancellationToken;
        var connectionString = await IsolatedMigratedConnectionStringAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var tenantA = Guid.Parse(TenantId);
        var tenantB = Guid.CreateVersion7();

        var complianceA = new TagBuilder().WithTenantId(tenantA).WithName("Compliance").Build();
        var complianceB = new TagBuilder().WithTenantId(tenantB).WithName(" COMPLIANCE ").Build();
        var dueWithEvidence = DueTask(tenantA, "Compliance job A with evidence", now.AddDays(1), complianceA);
        var dueWithoutEvidence = DueTask(tenantA, "Compliance job A without evidence", now.AddDays(2), complianceA);
        await using (var seed = DbContainerFixture.CreateTrxnContext(connectionString))
        {
            seed.Tags.AddRange(complianceA, complianceB);
            seed.TaskItems.AddRange(
                dueWithEvidence,
                dueWithoutEvidence,
                DueTask(tenantA, "Compliance job A untagged", now.AddDays(1)),
                DueTask(tenantB, "Compliance job B due", now.AddDays(2), complianceB));
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);
        }

        var reads = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var factory = new FlowEngineWorkflowApiFactory(
            connectionString,
            _ => """{"status":"expiringSoon","summary":"expires next week"}""",
            configureServices: services => RecordDocumentReads(services, reads));
        using var client = factory.CreateClient();
        var evidence = await UploadAttachmentAsync(client, dueWithEvidence.Id.Value, "evidence.txt", "text/plain", "certificate expires next week", ct);

        // The scheduled 06:10 run; the seeded due dates sit inside the 7-day window whatever the time of day the test runs.
        var scheduledRun = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddHours(6).AddMinutes(10);
        await RunComplianceCheckJobAsync(factory, connectionString, scheduledRun, ct);

        var store = factory.Services.GetRequiredService<IExecutionStateStore>();
        var instance = (await ComplianceCheckInstancesAsync(store, ct)).Single();
        Assert.AreEqual(tenantA.ToString(), instance.TenantId, "only the scaffold tenant is started; tenant B qualifies but is not");
        var (node, body) = await WaitForTerminalAsync(client, instance.InstanceId, ct);
        var children = await ChildInstancesAsync(client, "compliance-check-item", ct);
        var diagnostics = $"Reads: [{string.Join(", ", reads)}]; evidence {evidence}; children: {Describe(children)}. Parent: {Truncate(body)}";

        Assert.AreEqual("n-output-ok", node, diagnostics);
        Assert.AreEqual(2, DueTasksScanned(body), "the instance scans the scaffold tenant's two due compliance tasks. " + diagnostics);
        CollectionAssert.AreEquivalent(
            new[] { "Compliance job A with evidence -> n-done", "Compliance job A without evidence -> n-done" },
            children.Select(c => $"{c.Item} -> {c.At}").ToArray(),
            "one child per due compliance task of the scaffold tenant. " + diagnostics);
        CollectionAssert.AreEqual(new[] { evidence.ToString() }, reads.ToArray(),
            "the job-started instance carries the tenant, so its child reads the task's evidence. " + diagnostics);
        Assert.AreEqual(1, await CountCommentsAsync(client, dueWithEvidence.Id.Value, ct), "the expiring task gets one reminder. " + diagnostics);

        // Same UTC day, after another workflow's instance was saved, as any other start between two runs does, so the
        // engine's key lookup has to find an instance that is not the newest in the store.
        var now2 = DateTimeOffset.UtcNow;
        await store.SaveAsync(new ExecutionInstance
        {
            InstanceId = Guid.CreateVersion7().ToString("N"),
            WorkflowId = "ai-task-triage",
            TenantId = tenantA.ToString(),
            Status = ExecStatus.Completed,
            CreatedAt = now2,
            UpdatedAt = now2,
            CompletedAt = now2,
        }, ct);
        await RunComplianceCheckJobAsync(factory, connectionString, scheduledRun.AddHours(10), ct);
        var afterRerun = await ComplianceCheckInstancesAsync(store, ct);
        Assert.HasCount(1, afterRerun, "a same-day re-run resolves to the day's instance. "
            + string.Join(" | ", afterRerun.Select(i => $"{i.InstanceId} tenant={i.TenantId} created={i.CreatedAt:O}")));
    }

    /// <summary>
    /// D-075: an admin start of compliance-check with <c>tenantId</c> set to the scaffold tenant runs as that tenant. The
    /// scaffold principal resolves to no Admin API tenant (Scaffold mode reads <c>flowengine_tenant_id</c>, which it does
    /// not carry), so the package honours the field: the instance carries the tenant, its child reads the task's evidence
    /// and the expiring task gets its reminder.
    /// </summary>
    [TestMethod]
    public async Task ComplianceCheck_AdminStartWithTheScaffoldTenant_ChildReadsTheEvidence()
    {
        SkipIfNoSql();
        var ct = TestContext.CancellationToken;
        var connectionString = await IsolatedMigratedConnectionStringAsync(ct);
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

        var reads = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var factory = new FlowEngineWorkflowApiFactory(
            connectionString,
            _ => """{"status":"expiringSoon","summary":"expires next week"}""",
            configureServices: services => RecordDocumentReads(services, reads));
        using var client = factory.CreateClient();
        var evidence = await UploadAttachmentAsync(client, due.Id.Value, "evidence.txt", "text/plain", "certificate expires next week", ct);

        var instanceId = await StartWorkflowAsync(client, ComplianceCheckHandler.WorkflowId, ComplianceCheckParams(TenantId, now), ct, tenantId: TenantId);
        var (node, body) = await WaitForTerminalAsync(client, instanceId, ct);
        var children = await ChildInstancesAsync(client, "compliance-check-item", ct);
        var diagnostics = $"Reads: [{string.Join(", ", reads)}]; evidence {evidence}; children: {Describe(children)}. Parent: {Truncate(body)}";

        var instance = await factory.Services.GetRequiredService<IExecutionStateStore>().LoadAsync(instanceId, ct);
        Assert.AreEqual(TenantId, instance?.TenantId, "the admin start carries the requested tenant. " + diagnostics);
        Assert.AreEqual("n-output-ok", node, diagnostics);
        CollectionAssert.AreEqual(new[] { "Admin start with evidence -> n-done" }, children.Select(c => $"{c.Item} -> {c.At}").ToArray(), diagnostics);
        CollectionAssert.AreEqual(new[] { evidence.ToString() }, reads.ToArray(), "the child reads the evidence as the instance tenant. " + diagnostics);
        Assert.AreEqual(1, await CountCommentsAsync(client, due.Id.Value, ct), "the expiring task gets one reminder. " + diagnostics);
    }

    /// <summary>
    /// D-075: the scaffold principal carries tenant <c>0001</c> and <c>GlobalAdmin</c>, but the Admin API reads its tenant
    /// from <c>flowengine_tenant_id</c> in Scaffold mode, so the principal is a caller without a tenant and the package
    /// honours any <c>tenantId</c>: an admin start for another tenant is accepted and the instance carries that tenant,
    /// not the principal's. A caller with an Admin API tenant would get 403 for a different one.
    /// </summary>
    [TestMethod]
    public async Task ComplianceCheck_AdminStartWithAnotherTenant_ByTheScaffoldPrincipal_StartsInThatTenant()
    {
        SkipIfNoSql();
        var ct = TestContext.CancellationToken;
        var connectionString = await IsolatedMigratedConnectionStringAsync(ct);
        var other = Guid.CreateVersion7().ToString();
        using var factory = new FlowEngineWorkflowApiFactory(connectionString, _ => "{}");
        using var client = factory.CreateClient();

        var instanceId = await StartWorkflowAsync(client, ComplianceCheckHandler.WorkflowId, ComplianceCheckParams(other, DateTimeOffset.UtcNow), ct, tenantId: other);
        var (_, body) = await WaitForTerminalAsync(client, instanceId, ct);

        var instance = await factory.Services.GetRequiredService<IExecutionStateStore>().LoadAsync(instanceId, ct);
        Assert.AreEqual(other, instance?.TenantId, "a caller without an Admin API tenant starts in the requested tenant. " + Truncate(body));
    }

    private static Dictionary<string, object?> ComplianceCheckParams(string tenantId, DateTimeOffset now) => new()
    {
        ["tenantId"] = tenantId,
        ["dueBefore"] = now.AddDays(7).ToString("O", System.Globalization.CultureInfo.InvariantCulture),
    };

    private static async Task<List<ExecutionInstance>> ComplianceCheckInstancesAsync(IExecutionStateStore store, CancellationToken ct) =>
        (await store.QueryAsync(new ExecutionQuery { WorkflowId = "compliance-check", Take = 50 }, ct)).Items.ToList();

    // The task count n-query-due stored as context.dueTasks: what the instance scanned (compliance.swept scannedCount).
    private static int DueTasksScanned(string instanceBody)
    {
        using var json = JsonDocument.Parse(instanceBody);
        return json.RootElement.GetProperty("context").GetProperty("context").GetProperty("dueTasks").GetProperty("value").GetArrayLength();
    }

    // The Scheduler's handler over the system repository and this host's engine, as the TickerQ job runs it.
    private static async Task RunComplianceCheckJobAsync(
        FlowEngineWorkflowApiFactory factory, string connectionString, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(connectionString);
        var handler = new ComplianceCheckHandler(
            new TaskItemSystemRepository(db),
            factory.Services.GetRequiredService<IFlowEngine>(),
            SchedulerTestTelemetry.Create(),
            new FixedClock(now),
            Options.Create(new ComplianceCheckSettings()),
            factory.Services.GetRequiredService<IOptions<SelfCallRelayOptions>>(),
            NullLogger<ComplianceCheckHandler>.Instance);
        await handler.HandleAsync(ct);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
