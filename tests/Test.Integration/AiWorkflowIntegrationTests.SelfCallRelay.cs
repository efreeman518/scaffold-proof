using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using EF.Auth.Relay;
using EF.Data.Contracts;
using EF.FlowEngine.Abstractions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Test.Integration.Infrastructure;
using Test.Support.Builders;

namespace Test.Integration;

public sealed partial class AiWorkflowIntegrationTests
{
    /// <summary>
    /// The workflow self-call relay (D-068) end to end, with the test-only app token scheme standing in for a deployment's:
    /// with <c>FlowEngine:SelfCall:TokenScope</c> set and the workflow host's caller id trusted, the ComplianceCheck job
    /// starts every qualifying tenant, and each instance's API calls act for that instance's tenant. Tenant B's instance
    /// scans B's two due compliance tasks only (the scaffold tenant's due task is not visible to it), reads B's evidence and
    /// reminds B's task; the scaffold tenant's instance scans its own task only. A re-run the same UTC day starts no
    /// second instance for either tenant.
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

        var complianceA = new TagBuilder().WithTenantId(tenantA).WithName("Compliance").Build();
        var complianceB = new TagBuilder().WithTenantId(tenantB).WithName(" COMPLIANCE ").Build();
        var dueA = DueTask(tenantA, "Relay A due", now.AddDays(1), complianceA);
        var dueBWithEvidence = DueTask(tenantB, "Relay B with evidence", now.AddDays(1), complianceB);
        var dueBWithoutEvidence = DueTask(tenantB, "Relay B without evidence", now.AddDays(2), complianceB);
        var evidenceB = TextAttachment(tenantB, dueBWithEvidence.Id.Value, "evidence-b.txt", "certificate expires next week");
        await using (var seed = DbContainerFixture.CreateTrxnContext(connectionString))
        {
            seed.Tags.AddRange(complianceA, complianceB);
            seed.TaskItems.AddRange(dueA, dueBWithEvidence, dueBWithoutEvidence, DueTask(tenantB, "Relay B untagged", now.AddDays(1)));
            seed.Attachments.Add(evidenceB.Row);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);
        }

        var reads = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var factory = new FlowEngineWorkflowApiFactory(
            connectionString,
            _ => """{"status":"expiringSoon","summary":"expires next week"}""",
            configureServices: services => RecordDocumentReads(services, reads),
            selfCallRelay: true);
        using var client = factory.CreateClient();
        await StoreBlobAsync(factory, evidenceB, ct);

        var scheduledRun = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero).AddHours(6).AddMinutes(10);
        await RunComplianceCheckJobAsync(factory, connectionString, scheduledRun, ct);

        var store = factory.Services.GetRequiredService<IExecutionStateStore>();
        var instances = await ComplianceCheckInstancesAsync(store, ct);
        CollectionAssert.AreEquivalent(new[] { tenantA.ToString(), tenantB.ToString() }, instances.Select(i => i.TenantId).ToArray(),
            "with the relay every qualifying tenant is started, each instance carrying its tenant");
        var scanned = new Dictionary<string, int>();
        var bodies = new List<string>();
        foreach (var instance in instances)
        {
            var (node, body) = await WaitForTerminalAsync(client, instance.InstanceId, ct);
            bodies.Add(Truncate(body));
            Assert.AreEqual("n-output-ok", node, Truncate(body));
            scanned[instance.TenantId!] = DueTasksScanned(body);
        }

        var children = await ChildInstancesAsync(client, "compliance-check-item", ct);
        var diagnostics = $"Reads: [{string.Join(", ", reads)}]; children: {Describe(children)}. Parents: {string.Join(" | ", bodies)}";
        Assert.AreEqual(2, scanned[tenantB.ToString()], "tenant B's instance scans B's two due compliance tasks and nothing of the scaffold tenant. " + diagnostics);
        Assert.AreEqual(1, scanned[tenantA.ToString()], "the scaffold tenant's instance scans its own task only. " + diagnostics);
        CollectionAssert.AreEquivalent(
            new[] { "Relay A due -> n-done", "Relay B with evidence -> n-done", "Relay B without evidence -> n-done" },
            children.Select(c => $"{c.Item} -> {c.At}").ToArray(),
            "one child per due compliance task, each in its own tenant. " + diagnostics);
        CollectionAssert.AreEqual(new[] { evidenceB.Row.Id.Value.ToString() }, reads.ToArray(),
            "tenant B's attachment search, made as B, finds B's evidence. " + diagnostics);
        Assert.AreEqual(1, await CountCommentsIgnoringTenantAsync(connectionString, dueBWithEvidence.Id, ct),
            "the reminder is posted to B's task as B. " + diagnostics);
        Assert.AreEqual(0, await CountCommentsIgnoringTenantAsync(connectionString, dueA.Id, ct), diagnostics);

        // Same UTC day: one tenant's instance is always older than the other's, the case the engine's own key lookup misses.
        await RunComplianceCheckJobAsync(factory, connectionString, scheduledRun.AddHours(10), ct);
        var afterRerun = await ComplianceCheckInstancesAsync(store, ct);
        Assert.HasCount(2, afterRerun, "a same-day re-run resolves to each tenant's instance of the day. "
            + string.Join(" | ", afterRerun.Select(i => $"{i.InstanceId} tenant={i.TenantId} created={i.CreatedAt:O}")));
    }

    /// <summary>
    /// The Api side of the relay a workflow self-call uses: a relay header from the trusted workflow caller acts for the
    /// relayed tenant alone (a search naming the scaffold tenant still returns only the relayed tenant's tasks), and the
    /// same header from a caller the Api does not trust is refused rather than applied.
    /// </summary>
    [TestMethod]
    public async Task RelayHeader_ActsForTheRelayedTenantOnly_AndIsRefusedFromAnUntrustedCaller()
    {
        SkipIfNoSql();
        var ct = TestContext.CancellationToken;
        var connectionString = await IsolatedMigratedConnectionStringAsync(ct);
        var tenantA = Guid.Parse(TenantId);
        var tenantB = Guid.CreateVersion7();
        await using (var seed = DbContainerFixture.CreateTrxnContext(connectionString))
        {
            seed.TaskItems.AddRange(
                DueTask(tenantA, "Relay direct A", DateTimeOffset.UtcNow.AddDays(1)),
                DueTask(tenantB, "Relay direct B", DateTimeOffset.UtcNow.AddDays(1)));
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.Throw, cancellationToken: ct);
        }

        using var factory = new FlowEngineWorkflowApiFactory(connectionString, _ => "{}", selfCallRelay: true);
        var relay = factory.Services.GetRequiredService<IOptions<ForwardedClaimsOptions>>().Value;
        using var trusted = factory.CreateClient();
        SelfCallRelayTestAuth.RelayAs(trusted, SelfCallRelayTestAuth.TrustedCaller, tenantB, relay);
        using var untrusted = factory.CreateClient();
        SelfCallRelayTestAuth.RelayAs(untrusted, SelfCallRelayTestAuth.UntrustedCaller, tenantB, relay);
        var search = new { filter = new { tenantId = tenantA }, pageSize = 50 };

        using var asB = await trusted.PostAsJsonAsync("/api/v1/task-items/search", search, ct);
        var asBBody = await asB.Content.ReadAsStringAsync(ct);
        using var refused = await untrusted.PostAsJsonAsync("/api/v1/task-items/search", search, ct);
        var refusedBody = await refused.Content.ReadAsStringAsync(ct);

        Assert.AreEqual(HttpStatusCode.OK, asB.StatusCode, Truncate(asBBody));
        CollectionAssert.AreEqual(new[] { "Relay direct B" }, Titles(asBBody),
            "the relayed caller reads its own tenant only, whatever tenant the filter names");
        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode, Truncate(refusedBody));
        Assert.DoesNotContain("Relay direct", refusedBody, "an untrusted caller's relay header is never applied");
    }

    private static string[] Titles(string searchBody)
    {
        using var json = JsonDocument.Parse(searchBody);
        return [.. json.RootElement.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("title").GetString()!)];
    }
}
