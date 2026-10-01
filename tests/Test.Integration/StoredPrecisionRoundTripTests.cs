using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// D-033: the domain normalizes effort and dates to the precision the database stores, so what is read back after a
/// round trip equals the domain value on the selected provider lane (PostgreSQL or SQL Server). Component tier.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class StoredPrecisionRoundTripTests
{
    /// <summary>Applies migrations once for the class.</summary>
    [ClassInitialize]
    public static async Task ClassInit(TestContext context)
    {
        if (IntegrationTestSetup.IsUnavailable(DbContainerFixture.StartupError)) return;
        await using var db = DbContainerFixture.CreateTrxnContext();
        await db.Database.MigrateAsync(context.CancellationToken);
    }

    /// <summary>Inconclusive without a container runtime; fails when the database container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("database", DbContainerFixture.StartupError);

    /// <summary>Stored effort and dates equal the domain values after a round trip.</summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task EffortAndDates_RoundTripEqualToTheDomainValues()
    {
        var tenantId = TenantId.From(Guid.NewGuid());
        var moment = new DateTimeOffset(2030, 5, 6, 7, 8, 9, TimeSpan.Zero).AddTicks(1234567);
        var task = TaskItem.Create(tenantId, "Precision").Value!;
        task.Update(estimatedEffort: 1.23456m, actualEffort: 2.005m);
        task.UpdateDateRange(moment, moment.AddDays(1));

        await using (var seed = DbContainerFixture.CreateTrxnContext())
        {
            seed.TaskItems.Add(task);
            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        await using var query = DbContainerFixture.CreateQueryContext();
        var stored = await query.TaskItems.AsNoTracking().SingleAsync(t => t.Id == task.Id, TestContext.CancellationToken);

        Assert.AreEqual(1.23m, stored.EstimatedEffort);
        Assert.AreEqual(2.01m, stored.ActualEffort);
        Assert.AreEqual(task.EstimatedEffort, stored.EstimatedEffort);
        Assert.AreEqual(task.ActualEffort, stored.ActualEffort);
        Assert.AreEqual(task.StartDate, stored.StartDate);
        Assert.AreEqual(task.DueDate, stored.DueDate);
        Assert.AreEqual(task.StartDate!.Value.Ticks, stored.StartDate!.Value.Ticks);
    }

    public TestContext TestContext { get; set; } = null!;
}
