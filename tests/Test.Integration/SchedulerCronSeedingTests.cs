extern alias SchedulerHost;

using EF.Audit.Contracts;
using EF.Common.Contracts;
using EF.Data.Scheduling;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SchedulerHost::TaskFlow.Scheduler;
using SchedulerHost::TaskFlow.Scheduler.Handlers;
using SchedulerHost::TaskFlow.Scheduler.Handlers.Retention;
using TaskFlow.Infrastructure.Data;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// The Scheduler's cron jobs reach the scheduler database at host start, on the selected provider lane: the executing
/// host publishes one catalog row per registered job with its cron, and the planner records one cron state row per cron
/// job, starting at the next occurrence. A restart (or a second replica) converges on the same rows and enqueues
/// nothing early. A triggered run executes its handler through the worker and leaves a history row, and the schema
/// validator refuses a database without the scheduler tables. The registration is the Scheduler's AddJobScheduler.
/// Component tier: a minimal host running the real scheduler registration against the database Testcontainer.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class SchedulerCronSeedingTests
{
    private static readonly Dictionary<string, string> ExpectedJobs = new(StringComparer.Ordinal)
    {
        [OverdueTaskCheckHandler.JobName] = "0 0 */6 * * *",
        [RecurringTaskGenerationHandler.JobName] = "0 0 2 * * *",
        [StaleTaskCleanupHandler.JobName] = "0 0 3 * * 0",
        [ComplianceCheckHandler.JobName] = "0 10 6 * * *",
        [OutboxRetentionHandler.JobName] = "0 15 * * * *",
        [ConsumerInboxRetentionHandler.JobName] = "0 20 * * * *",
        [RegisterSchedulerServices.SchedulerRetentionJobName] = "0 30 4 * * *",
        [AuditRetentionHandler.JobName] = "0 40 4 * * *"
    };

    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(90);

    /// <summary>Inconclusive without a container runtime; fails when the database container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("database", DbContainerFixture.StartupError);

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task HostStart_PublishesEveryDeclaredCronJob_AndARestartAddsNone()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("schedulerseed", ct);
        await using (var migrate = DbContainerFixture.CreateSchedulerContext(connString))
        {
            await migrate.Database.MigrateAsync(ct);
        }

        var firstStart = DateTimeOffset.UtcNow;
        await StartUntilPlannedAndStopAsync(connString, ct);
        await AssertPublishedOnceAsync(connString, firstStart, ct);

        // A second start (a restart, or a second replica) must converge on the same rows, not add more.
        await StartUntilPlannedAndStopAsync(connString, ct);
        await AssertPublishedOnceAsync(connString, firstStart, ct);
    }

    /// <summary>
    /// A run triggered through ISchedulerAdmin is claimed by the worker, which resolves the handler in its own scope
    /// (the audit sweep, over a recording store) and records the attempt.
    /// </summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task TriggeredJob_RunsItsHandler_AndRecordsTheRun()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("schedulerrun", ct);
        await using (var migrate = DbContainerFixture.CreateSchedulerContext(connString))
        {
            await migrate.Database.MigrateAsync(ct);
        }

        var audit = new RecordingAuditLog();
        using var host = BuildHost(connString, services =>
        {
            services.AddSingleton<IAuditLogRepository>(audit);
            services.AddOptions<AuditSettings>();
        });
        await SchedulerSchemaValidator.ValidateAsync<TaskFlowSchedulerDbContext>(host.Services, ct);
        await host.StartAsync(ct);
        try
        {
            Guid runId;
            await using (var scope = host.Services.CreateAsyncScope())
            {
                runId = await scope.ServiceProvider.GetRequiredService<ISchedulerAdmin>()
                    .TriggerAsync(AuditRetentionHandler.JobName, ct: ct);
            }

            await audit.Purged.Task.WaitAsync(Budget, ct);
            var run = await WaitForAsync(connString,
                db => db.Set<ScheduledJobRun>().AsNoTracking().SingleOrDefaultAsync(r => r.JobId == runId, ct), ct);
            Assert.AreEqual("success", run.Status, run.Error);
            Assert.AreEqual(AuditRetentionHandler.JobName, run.JobName);
        }
        finally
        {
            await host.StopAsync(ct);
        }
    }

    /// <summary>The Scheduler refuses a scheduler database without its tables; it never creates them.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task SchemaValidator_MissingTables_Throws()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("schedulernoschema", ct);
        using var host = BuildHost(connString);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => SchedulerSchemaValidator.ValidateAsync<TaskFlowSchedulerDbContext>(host.Services, ct));
        StringAssert.Contains(ex.Message, nameof(ScheduledJob));
    }

    public TestContext TestContext { get; set; } = null!;

    /// <summary>
    /// Runs the host until its planner has published the catalog and recorded the cron state of every cron job: the
    /// planner works on its first poll after start, not inside StartAsync.
    /// </summary>
    private static async Task StartUntilPlannedAndStopAsync(string connString, CancellationToken ct)
    {
        using var host = BuildHost(connString);
        await host.StartAsync(ct);
        try
        {
            await WaitForAsync(connString, async db =>
                await db.Set<CronJobState>().CountAsync(ct) >= ExpectedJobs.Count ? (object)true : null, ct);
        }
        finally
        {
            await host.StopAsync(ct);
        }
    }

    /// <summary>The Scheduler's scheduler registration on a bare host.</summary>
    private static IHost BuildHost(string connString, Action<IServiceCollection>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [$"ConnectionStrings:{RegisterSchedulerServices.SchedulerConnectionName}"] = connString,
            ["Scheduling:CronPollInterval"] = "00:00:01",
            ["Scheduling:PollInterval"] = "00:00:01",
            ["Scheduling:IdleBackoffMax"] = "00:00:01",
            ["Scheduling:Health:StallThreshold"] = "12:00:00"
        });
        builder.Services.AddLogging();
        configure?.Invoke(builder.Services);
        builder.AddJobScheduler();
        return builder.Build();
    }

    private static async Task<T> WaitForAsync<T>(
        string connString, Func<TaskFlowSchedulerDbContext, Task<T?>> read, CancellationToken ct)
        where T : class
    {
        var deadline = DateTimeOffset.UtcNow + Budget;
        while (true)
        {
            await using (var db = DbContainerFixture.CreateSchedulerContext(connString))
            {
                if (await read(db) is { } value) return value;
            }

            Assert.IsLessThan(deadline, DateTimeOffset.UtcNow, $"the scheduler did not reach the expected state within {Budget}");
            await Task.Delay(TimeSpan.FromMilliseconds(250), ct);
        }
    }

    /// <summary>Records the retention purge the audit job issues; nothing else is expected of the store here.</summary>
    private sealed class RecordingAuditLog : IAuditLogRepository
    {
        public TaskCompletionSource Purged { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task AppendAsync<TAuditIdType, TTenantIdType>(
            AuditEntry<TAuditIdType, TTenantIdType> entry, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AuditLogPage> QueryAsync(AuditLogQuery query, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<int> PurgeOlderThanAsync(DateTimeOffset cutoffUtc, CancellationToken cancellationToken = default)
        {
            Purged.TrySetResult();
            return Task.FromResult(0);
        }
    }

    private static async Task AssertPublishedOnceAsync(string connString, DateTimeOffset firstStart, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateSchedulerContext(connString);
        var definitions = await db.Set<ScheduledJobDefinition>().AsNoTracking()
            .Select(d => new { d.JobName, d.Cron, d.TimeZoneId })
            .ToListAsync(ct);
        var states = await db.Set<CronJobState>().AsNoTracking().Select(s => s.JobName).ToListAsync(ct);

        Assert.HasCount(ExpectedJobs.Count, definitions,
            $"expected one catalog row per job, found: {string.Join(", ", definitions.Select(d => d.JobName))}");
        foreach (var (name, cron) in ExpectedJobs)
        {
            var definition = definitions.SingleOrDefault(d => d.JobName == name);
            Assert.IsNotNull(definition, $"job {name} was not published");
            Assert.AreEqual(cron, definition.Cron, $"job {name} has the wrong schedule");
            Assert.AreEqual("UTC", definition.TimeZoneId, $"job {name} must read its cron in UTC");
        }

        CollectionAssert.AreEquivalent(ExpectedJobs.Keys.ToList(), states, "expected one cron state row per cron job");
        // A job seen for the first time starts at its next occurrence: no occurrence before the first start is enqueued.
        var early = await db.Set<ScheduledJob>().AsNoTracking()
            .Where(j => j.CronOccurrenceUtc < firstStart)
            .Select(j => j.JobName)
            .ToListAsync(ct);
        Assert.IsEmpty(early, $"a host start enqueued past occurrences of: {string.Join(", ", early)}");
    }
}
