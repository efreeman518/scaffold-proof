extern alias SchedulerHost;

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SchedulerHost::TaskFlow.Scheduler;
using TickerQ.DependencyInjection;
using TickerQ.Utilities.Entities;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// The Scheduler's cron jobs reach the TickerQ operational store at host start, on the selected provider lane.
/// They used to be "seeded" by a call that ran before TickerQ had registered any function, so every insert was
/// rejected, the rejection ignored, and no job ever ran. The restart half is the other failure mode: seeding by
/// hand after start would insert every job again on each restart and each replica.
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
        ["OutboxRetention"] = "0 15 * * * *",
        ["ConsumerInboxRetention"] = "0 20 * * * *",
        ["TickerQOccurrenceRetention"] = "0 30 4 * * *",
        ["AuditRetention"] = "0 40 4 * * *"
    };

    /// <summary>Marks the test Inconclusive when the database container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("database", DbContainerFixture.StartupError);

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task HostStart_SeedsEveryDeclaredCronJob_AndARestartAddsNone()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("tickerqseed");
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

    public TestContext TestContext { get; set; } = null!;

    private static async Task StartAndStopAsync(string connString, CancellationToken ct)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:TickerQDbContext"] = connString,
            ["Scheduling:UsePersistence"] = "true"
        });
        builder.Services.AddLogging();
        builder.AddTickerQConfig();

        using var host = builder.Build();
        host.UseTickerQ();
        await host.StartAsync(ct);
        await host.StopAsync(ct);
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
