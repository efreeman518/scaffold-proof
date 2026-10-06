extern alias SchedulerHost;

using EF.Audit.Contracts;
using EF.BackgroundServices.TickerQ;
using EF.Common;
using EF.Common.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SchedulerHost::TaskFlow.Scheduler;
using SchedulerHost::TaskFlow.Scheduler.Handlers.Retention;
using SchedulerHost::TaskFlow.Scheduler.Jobs;
using TaskFlow.Infrastructure.Data;
using TickerQ.DependencyInjection;
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Interfaces.Managers;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// The Scheduler's cron jobs reach the TickerQ operational store at host start, on the selected provider lane.
/// They used to be "seeded" by a call that ran before TickerQ had registered any function, so every insert was
/// rejected, the rejection ignored, and no job ever ran. The restart half is the other failure mode: seeding by
/// hand after start would insert every job again on each restart and each replica. The registration is
/// EF.BackgroundServices.TickerQ AddEFTickerQ: seeding runs inside the distributed seed lock, a due ticker runs its job
/// class through ScheduledJobRunner, and the schema validator refuses a database without the TickerQ tables.
/// Component tier: a minimal host running the real TickerQ registration against the database Testcontainer.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class SchedulerCronSeedingTests
{
    private static readonly Dictionary<string, string> ExpectedJobs = new(StringComparer.Ordinal)
    {
        ["OverdueTaskCheck"] = "0 0 */6 * * *",
        ["RecurringTaskGeneration"] = "0 0 2 * * *",
        ["StaleTaskCleanup"] = "0 0 3 * * 0",
        ["ComplianceCheck"] = "0 10 6 * * *",
        ["OutboxRetention"] = "0 15 * * * *",
        ["ConsumerInboxRetention"] = "0 20 * * * *",
        ["TickerQOccurrenceRetention"] = "0 30 4 * * *",
        ["AuditRetention"] = "0 40 4 * * *"
    };

    /// <summary>Inconclusive without a container runtime; fails when the database container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("database", DbContainerFixture.StartupError);

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task HostStart_SeedsEveryDeclaredCronJob_AndARestartAddsNone()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("tickerqseed", ct);
        await using (var migrate = DbContainerFixture.CreateTickerQContext(connString))
        {
            await migrate.Database.MigrateAsync(ct);
        }

        await StartAndStopAsync(connString, ct);
        await AssertSeededOnceAsync(connString, ct);

        // A second start (a restart, or a second replica) must converge on the same rows, not add more.
        await StartAndStopAsync(connString, ct);
        await AssertSeededOnceAsync(connString, ct);
    }

    /// <summary>
    /// A due ticker for a declared function runs: TickerQ's generated delegate resolves TaskMaintenanceJobs in the
    /// execution scope, and its ScheduledJobRunner runs the handler there (the audit sweep, over a recording store).
    /// </summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task DueTicker_RunsItsJobThroughTheRunner()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("tickerqrun", ct);
        await using (var migrate = DbContainerFixture.CreateTickerQContext(connString))
        {
            await migrate.Database.MigrateAsync(ct);
        }

        var audit = new RecordingAuditLog();
        using var host = BuildHost(connString, services =>
        {
            services.AddSingleton<IAuditLogRepository>(audit);
            services.AddOptions<AuditSettings>();
            services.AddScoped<AuditRetentionHandler>();
            services.AddScoped<TaskMaintenanceJobs>();
        });
        host.UseTickerQ();
        await host.StartAsync(ct);
        try
        {
            await TickerQSchemaValidator.ValidateAsync<TaskFlowTickerQDbContext>(host.Services, ct);

            await using (var scope = host.Services.CreateAsyncScope())
            {
                var tickers = scope.ServiceProvider.GetRequiredService<ITimeTickerManager<TimeTickerEntity>>();
                var added = await tickers.AddAsync(
                    new TimeTickerEntity { Function = AuditRetentionHandler.JobName, ExecutionTime = DateTime.UtcNow.AddSeconds(1) }, ct);
                Assert.IsTrue(added.IsSucceeded, "TickerQ rejected the time ticker");
            }

            await audit.Purged.Task.WaitAsync(TimeSpan.FromSeconds(90), ct);
        }
        finally
        {
            await host.StopAsync(ct);
        }
    }

    /// <summary>The Scheduler refuses an operational store without the TickerQ tables; it never creates them.</summary>
    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task SchemaValidator_MissingTables_Throws()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("tickerqnoschema", ct);
        using var host = BuildHost(connString);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => TickerQSchemaValidator.ValidateAsync<TaskFlowTickerQDbContext>(host.Services, ct));
        StringAssert.Contains(ex.Message, "CronTickers");
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task StartAndStopAsync(string connString, CancellationToken ct)
    {
        using var host = BuildHost(connString);
        host.UseTickerQ();
        await host.StartAsync(ct);
        await host.StopAsync(ct);
    }

    /// <summary>
    /// The Scheduler's TickerQ registration on a bare host. The seed lock needs an IDistributedLock, which the
    /// Scheduler gets from EF.Cache; the in-process one stands in for a single replica.
    /// </summary>
    private static IHost BuildHost(string connString, Action<IServiceCollection>? configure = null)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TickerQDbContext"] = connString,
            ["Scheduling:UsePersistence"] = "true",
            ["Scheduling:PollIntervalSeconds"] = "1",
            ["Scheduling:Health:StallThreshold"] = "12:00:00"
        });
        builder.Services.AddLogging();
        builder.Services.AddSingleton<IDistributedLock, InProcessDistributedLock>();
        configure?.Invoke(builder.Services);
        builder.AddTickerQConfig();
        return builder.Build();
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

    private static async Task AssertSeededOnceAsync(string connString, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTickerQContext(connString);
        var tickers = await db.Set<CronTickerEntity>().AsNoTracking()
            .Select(t => new { t.Function, t.Expression })
            .ToListAsync(ct);

        Assert.HasCount(ExpectedJobs.Count, tickers,
            $"expected one cron ticker per job, found: {string.Join(", ", tickers.Select(t => t.Function))}");
        foreach (var (function, expression) in ExpectedJobs)
        {
            var ticker = tickers.SingleOrDefault(t => t.Function == function);
            Assert.IsNotNull(ticker, $"cron job {function} was not seeded");
            Assert.AreEqual(expression, ticker.Expression, $"cron job {function} has the wrong schedule");
        }
    }
}
