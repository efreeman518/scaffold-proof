using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TaskFlow.Bootstrapper.HealthChecks;
using TaskFlow.Infrastructure.Data;

namespace Test.Unit.Infrastructure;

/// <summary>
/// <c>CanConnectAsync</c> answers an unreachable database with false, not an exception, so a check that ignores
/// its result reports Healthy with the database down - and readiness keeps routing traffic to it.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SqlHealthCheckTests
{
    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    [DataRow(HealthStatus.Unhealthy)]
    [DataRow(HealthStatus.Degraded)]
    public async Task UnreachableDatabase_ReportsTheRegistrationFailureStatus(HealthStatus failureStatus)
    {
        // A port nothing listens on: the connection is refused at once, and CanConnectAsync returns false.
        var options = new DbContextOptionsBuilder<TaskFlowDbContextTrxn>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=taskflow;Username=u;Password=p;Timeout=2")
            .Options;
        var check = new SqlHealthCheck(new Factory(options));
        var context = new HealthCheckContext
        {
            Registration = new HealthCheckRegistration("sql", check, failureStatus, tags: null)
        };

        var result = await check.CheckHealthAsync(context, TestContext.CancellationToken);

        Assert.AreEqual(failureStatus, result.Status);
    }

    private sealed class Factory(DbContextOptions<TaskFlowDbContextTrxn> options) : IDbContextFactory<TaskFlowDbContextTrxn>
    {
        public TaskFlowDbContextTrxn CreateDbContext() => new(options) { AuditId = "sql-health-check-test" };
    }
}
