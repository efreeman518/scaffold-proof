using System.Runtime.CompilerServices;
using TaskFlow.Scheduler.Handlers;
using TaskFlow.Scheduler.Handlers.Retention;
using TaskFlow.Scheduler.Jobs;
using TickerQ.Utilities;

namespace Test.Unit.Hosting;

/// <summary>
/// Every Scheduler cron job carries its schedule in its <c>[TickerFunction]</c> registration. TickerQ seeds the
/// operational store only from functions that have a cron expression, so a job registered without one is never
/// scheduled - which is what happened while the expressions lived in a manual seeding call that ran before
/// TickerQ knew any function. The store round trip is proven in Test.Integration SchedulerCronSeedingTests.
/// Pure-unit tier: reads TickerQ's in-process function registry, no database.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SchedulerCronRegistrationTests
{
    [TestMethod]
    [DataRow(OverdueTaskCheckHandler.JobName, "0 0 */6 * * *")]
    [DataRow(RecurringTaskGenerationHandler.JobName, "0 0 2 * * *")]
    [DataRow(StaleTaskCleanupHandler.JobName, "0 0 3 * * 0")]
    [DataRow(ComplianceCheckHandler.JobName, "0 10 6 * * *")]
    [DataRow(OutboxRetentionHandler.JobName, "0 15 * * * *")]
    [DataRow(ConsumerInboxRetentionHandler.JobName, "0 20 * * * *")]
    [DataRow(TaskMaintenanceJobs.TickerQOccurrenceRetention, "0 30 4 * * *")]
    [DataRow(AuditRetentionHandler.JobName, "0 40 4 * * *")]
    public void Given_SchedulerAssembly_When_TickerFunctionsBuilt_Then_JobCarriesItsCronExpression(
        string jobName, string expectedCron)
    {
        var functions = RegisteredFunctions();

        Assert.IsTrue(functions.TryGetValue(jobName, out var function), $"{jobName} is not a registered TickerFunction");
        Assert.AreEqual(expectedCron, function.cronExpression,
            $"{jobName} has no schedule on its TickerFunction; TickerQ would never seed it");
    }

    [TestMethod]
    public void Given_SchedulerAssembly_When_TickerFunctionsBuilt_Then_NoJobIsLeftWithoutASchedule()
    {
        var unscheduled = RegisteredFunctions()
            .Where(f => f.Value.Delegate is not null && string.IsNullOrWhiteSpace(f.Value.cronExpression))
            .Select(f => f.Key)
            .ToList();

        Assert.IsEmpty(unscheduled, $"cron jobs without a schedule: {string.Join(", ", unscheduled)}");
    }

    private static IReadOnlyDictionary<string, (string cronExpression, TickerQ.Utilities.Enums.TickerTaskPriority Priority, TickerFunctionDelegate Delegate, int MaxConcurrency)> RegisteredFunctions()
    {
        // The TickerQ source generator registers the functions from a module initializer; running it explicitly
        // makes the test independent of whether anything else touched the Scheduler assembly first. Build is what
        // TickerQ's initializer calls at host start, and is idempotent.
        RuntimeHelpers.RunModuleConstructor(typeof(TaskMaintenanceJobs).Module.ModuleHandle);
        TickerFunctionProvider.Build();
        return TickerFunctionProvider.TickerFunctions;
    }
}
