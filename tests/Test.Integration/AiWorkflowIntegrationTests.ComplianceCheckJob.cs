extern alias SchedulerHost;

using EF.Data.Contracts;
using EF.FlowEngine.Abstractions;
using EF.FlowEngine.Model;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SchedulerHost::TaskFlow.Scheduler.Handlers;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support.Builders;

namespace Test.Integration;

public sealed partial class AiWorkflowIntegrationTests
{
    /// <summary>
    /// The start path of compliance-check (D-075): the Scheduler's ComplianceCheck job, run twice on the same day against
    /// seeded tenants, starts exactly one compliance-check instance per tenant with an open task tagged "compliance"
    /// (trimmed, any case) due within the window, each carrying that tenant, and none for a tenant whose only tagged
    /// task is completed, due after the window, or tagged otherwise. The scaffold tenant's instance then runs end to end
    /// and its child reads the task's evidence, which an instance without a tenant is refused (see
    /// <see cref="ComplianceCheckItem_StartedWithoutATenant_IsRefusedItsEvidence"/>).
    /// </summary>
    [TestMethod]
    public async Task ComplianceCheckJob_StartsOneInstancePerQualifyingTenant_CarryingThatTenant()
    {
        SkipIfNoSql();
        var ct = TestContext.CancellationToken;
        var connectionString = await IsolatedMigratedConnectionStringAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var tenantA = Guid.Parse(TenantId);
        var tenantB = Guid.CreateVersion7();
        var completedOnly = Guid.CreateVersion7();
        var dueLater = Guid.CreateVersion7();
        var otherTag = Guid.CreateVersion7();

        var complianceA = new TagBuilder().WithTenantId(tenantA).WithName("Compliance").Build();
        var complianceB = new TagBuilder().WithTenantId(tenantB).WithName(" COMPLIANCE ").Build();
        var complianceCompleted = new TagBuilder().WithTenantId(completedOnly).WithName("compliance").Build();
        var complianceLater = new TagBuilder().WithTenantId(dueLater).WithName("compliance").Build();
        var audit = new TagBuilder().WithTenantId(otherTag).WithName("audit").Build();
        var dueA = DueTask(tenantA, "Compliance job A due", now.AddDays(1), complianceA);
        var completed = DueTask(completedOnly, "Compliance job completed", now.AddDays(1), complianceCompleted);
        Assert.IsTrue(completed.TransitionStatus(TaskItemStatus.InProgress).IsSuccess);
        Assert.IsTrue(completed.TransitionStatus(TaskItemStatus.Completed).IsSuccess);
        await using (var seed = DbContainerFixture.CreateTrxnContext(connectionString))
        {
            seed.Tags.AddRange(complianceA, complianceB, complianceCompleted, complianceLater, audit);
            seed.TaskItems.AddRange(
                dueA,
                DueTask(tenantA, "Compliance job A untagged", now.AddDays(1)),
                // Two due tasks, one instance: the job starts tenants, the workflow walks their tasks.
                DueTask(tenantB, "Compliance job B first", now.AddDays(2), complianceB),
                DueTask(tenantB, "Compliance job B second", now.AddDays(3), complianceB),
                completed,
                DueTask(dueLater, "Compliance job due after the window", now.AddDays(30), complianceLater),
                DueTask(otherTag, "Compliance job other tag", now.AddDays(1), audit));
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);
        }

        var reads = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var factory = new FlowEngineWorkflowApiFactory(
            connectionString,
            _ => """{"status":"expiringSoon","summary":"expires next week"}""",
            configureServices: services => RecordDocumentReads(services, reads));
        using var client = factory.CreateClient();
        var evidence = await UploadAttachmentAsync(client, dueA.Id.Value, "evidence.txt", "text/plain", "certificate expires next week", ct);

        // The scheduled 06:10 run and a re-run later the same UTC day, which resolves to the instances the first run
        // started (one idempotency key per tenant and day). Both run times keep every seeded due date inside or outside
        // the 7-day window as labelled, whatever the time of day the test runs.
        var scheduledRun = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddHours(6).AddMinutes(10);
        await RunComplianceCheckJobAsync(factory, connectionString, scheduledRun, ct);
        await RunComplianceCheckJobAsync(factory, connectionString, scheduledRun.AddHours(10), ct);

        var store = factory.Services.GetRequiredService<IExecutionStateStore>();
        var instances = (await store.QueryAsync(new ExecutionQuery { WorkflowId = "compliance-check", Take = 50 }, ct)).Items.ToList();
        CollectionAssert.AreEquivalent(
            new[] { tenantA.ToString(), tenantB.ToString() },
            instances.Select(i => i.TenantId).ToArray(),
            "one instance per qualifying tenant, carrying that tenant, and none for a completed, later or differently tagged task. "
            + string.Join(" | ", instances.Select(i => $"{i.InstanceId} tenant={i.TenantId} created={i.CreatedAt:O} tags={string.Join(",", (i.Tags ?? new Dictionary<string, string>()).Select(t => t.Key + "=" + t.Value))}")));

        var nodes = new Dictionary<string, string>();
        foreach (var instance in instances)
        {
            var (node, body) = await WaitForTerminalAsync(client, instance.InstanceId, ct);
            nodes[instance.TenantId!] = node;
            TestContext.WriteLine($"{instance.TenantId}: {node} {Truncate(body)}");
        }

        var children = await ChildInstancesAsync(client, "compliance-check-item", ct);
        var diagnostics = $"Reads: [{string.Join(", ", reads)}]; evidence {evidence}; children: {Describe(children)}.";
        Assert.AreEqual("n-output-ok", nodes[tenantA.ToString()], diagnostics);
        Assert.AreEqual("n-output-ok", nodes[tenantB.ToString()], diagnostics);
        CollectionAssert.AreEqual(new[] { evidence.ToString() }, reads.ToArray(),
            "the job-started instance carries tenant A, so its child reads the task's evidence. " + diagnostics);
        Assert.AreEqual(1, await CountCommentsAsync(client, dueA.Id.Value, ct), "the expiring task gets one reminder. " + diagnostics);
    }

    // The Scheduler's handler over the system repository and this host's engine, as the TickerQ job runs it.
    private static async Task RunComplianceCheckJobAsync(
        FlowEngineWorkflowApiFactory factory, string connectionString, DateTimeOffset now, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(connectionString);
        var handler = new ComplianceCheckHandler(
            new TaskItemSystemRepository(db),
            factory.Services.GetRequiredService<IFlowEngine>(),
            factory.Services.GetRequiredService<IExecutionStateStore>(),
            SchedulerTestTelemetry.Create(),
            new FixedClock(now),
            Options.Create(new ComplianceCheckSettings()),
            NullLogger<ComplianceCheckHandler>.Instance);
        await handler.HandleAsync(ct);
    }

    private sealed class FixedClock(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
