using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Scheduler.Infrastructure;
using TickerQ.Utilities.Entities;

namespace Test.Unit.Hosting;

/// <summary>
/// The scheduler health check has to catch a scheduler that runs but fires nothing. It used to report Healthy
/// whenever no occurrence had ever executed, which is exactly how a Scheduler whose cron jobs were never seeded
/// stayed green in every environment.
/// Pure-unit tier: EF InMemory operational store and a fixed clock.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SchedulerHealthCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 28, 12, 0, 0, TimeSpan.Zero);

    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Given_NoExecutionEver_When_UptimeExceedsThreshold_Then_Degraded()
    {
        var result = await CheckAsync(startedAgo: SchedulerHealthCheck.StallThreshold + TimeSpan.FromMinutes(1), lastExecutedAgo: null);

        Assert.AreEqual(HealthStatus.Degraded, result.Status, result.Description);
    }

    [TestMethod]
    public async Task Given_NoExecutionYet_When_InsideTheGraceWindow_Then_Healthy()
    {
        var result = await CheckAsync(startedAgo: TimeSpan.FromHours(1), lastExecutedAgo: null);

        Assert.AreEqual(HealthStatus.Healthy, result.Status, result.Description);
    }

    [TestMethod]
    public async Task Given_RecentExecution_When_Checked_Then_Healthy()
    {
        var result = await CheckAsync(startedAgo: TimeSpan.FromDays(3), lastExecutedAgo: TimeSpan.FromHours(1));

        Assert.AreEqual(HealthStatus.Healthy, result.Status, result.Description);
    }

    [TestMethod]
    public async Task Given_StaleExecution_When_Checked_Then_Degraded()
    {
        var result = await CheckAsync(
            startedAgo: TimeSpan.FromDays(3), lastExecutedAgo: SchedulerHealthCheck.StallThreshold + TimeSpan.FromMinutes(1));

        Assert.AreEqual(HealthStatus.Degraded, result.Status, result.Description);
    }

    private async Task<HealthCheckResult> CheckAsync(TimeSpan startedAgo, TimeSpan? lastExecutedAgo)
    {
        var ct = TestContext.CancellationToken;
        var options = new DbContextOptionsBuilder<TaskFlowTickerQDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new TaskFlowTickerQDbContext(options);
        if (lastExecutedAgo is { } ago)
        {
            var ticker = new CronTickerEntity { Id = Guid.NewGuid(), Function = "OverdueTaskCheck", Expression = "0 0 */6 * * *", Request = [] };
            db.Add(ticker);
            db.Add(new CronTickerOccurrenceEntity<CronTickerEntity>
            {
                Id = Guid.NewGuid(),
                CronTickerId = ticker.Id,
                LockHolder = "test",
                ExecutionTime = (Now - ago).UtcDateTime,
                ExecutedAt = (Now - ago).UtcDateTime
            });
            await db.SaveChangesAsync(ct);
        }

        var clock = new FixedClock(Now);
        var startTime = new SchedulerStartTime(new FixedClock(Now - startedAgo));
        await startTime.StartAsync(ct);

        var services = new ServiceCollection().AddSingleton(db).BuildServiceProvider();
        var check = new SchedulerHealthCheck(services, startTime, clock);
        return await check.CheckHealthAsync(new HealthCheckContext(), ct);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
