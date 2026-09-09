using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support;

namespace Test.Integration;

/// <summary>
/// Asserts the dashboard summary is a single database round trip. The projection is one
/// <c>GroupBy(_ => 1)</c> with conditional aggregation precisely so that it does not become one query per
/// status; a refactor that reads naturally but issues seven queries would pass every other test in the suite
/// and quietly multiply the dashboard's cost by seven. A command interceptor is the only way to see that.
/// Component tier: real database on the selected provider lane.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class TaskSummaryRoundTripTests
{
    /// <summary>Applies migrations once for the class.</summary>
    [ClassInitialize]
    public static async Task ClassInit(TestContext context)
    {
        if (IntegrationTestSetup.IsUnavailable(DbContainerFixture.StartupError)) return;
        await using var db = DbContainerFixture.CreateTrxnContext();
        await db.Database.MigrateAsync(context.CancellationToken);
    }

    /// <summary>Marks the test Inconclusive when the database container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("database", DbContainerFixture.StartupError);

    /// <summary>One SELECT for the whole summary, with the counts the dashboard shows.</summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task GetSummaryAsync_IssuesExactlyOneQuery()
    {
        var tenantId = Guid.NewGuid();
        var typedTenantId = DomainId.From<TenantId>(tenantId);

        await using (var seed = DbContainerFixture.CreateTrxnContext())
        {
            for (var i = 0; i < 5; i++)
            {
                var task = TaskItem.Create(typedTenantId, $"Summary task {i}").Value!;
                if (i % 2 == 0) task.UpdateDateRange(null, DateTimeOffset.UtcNow.AddDays(-1));
                seed.TaskItems.Add(task);
            }

            var completed = TaskItem.Create(typedTenantId, "Completed task").Value!;
            completed.TransitionStatus(TaskItemStatus.InProgress);
            completed.TransitionStatus(TaskItemStatus.Completed);
            seed.TaskItems.Add(completed);

            await seed.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: TestContext.CancellationToken);
        }

        var counter = new CommandCountingInterceptor();
        await using var query = DbContainerFixture.CreateQueryContext(null, counter);

        var summary = await new TaskItemRepositoryQuery(query, TestColumnEncryption.Keys)
            .GetSummaryAsync(tenantId, TestContext.CancellationToken);

        Assert.AreEqual(1, counter.Commands, "the summary must be one round trip, not one query per status");
        Assert.AreEqual(6, summary.Total);
        Assert.AreEqual(3, summary.Overdue);
        Assert.AreEqual(1, summary.ByStatus.First(s => s.Status == TaskItemStatus.Completed).Count);
    }

    /// <summary>Counts executed commands on the context it is attached to.</summary>
    private sealed class CommandCountingInterceptor : DbCommandInterceptor
    {
        private int _commands;

        public int Commands => _commands;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
        {
            Interlocked.Increment(ref _commands);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _commands);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    public TestContext TestContext { get; set; } = null!;
}
