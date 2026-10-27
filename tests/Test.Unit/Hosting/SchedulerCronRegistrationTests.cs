using EF.Data.Scheduling;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using TaskFlow.Scheduler;
using TaskFlow.Scheduler.Handlers;
using TaskFlow.Scheduler.Handlers.Retention;

namespace Test.Unit.Hosting;

/// <summary>
/// Every Scheduler cron job carries its schedule in its EF.Data.Scheduling registration, after the shipped
/// <c>Scheduling:Jobs:{name}</c> configuration is applied. The planner enqueues occurrences only for a job with a cron,
/// so a job registered without one would never run. The database round trip (catalog publication and the run) is
/// proven in Test.Integration SchedulerCronSeedingTests.
/// Pure-unit tier: the Scheduler's registration over the shipped appsettings, no database.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class SchedulerCronRegistrationTests
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

    [TestMethod]
    public void Given_SchedulerRegistration_When_JobOptionsResolved_Then_EveryJobCarriesItsCronInUtc()
    {
        using var provider = BuildSchedulerServices();
        var jobs = provider.GetRequiredService<IOptionsMonitor<ScheduledJobOptions>>();

        foreach (var (name, cron) in ExpectedJobs)
        {
            // Get validates the job's options (a cron that cannot be parsed or never matches throws).
            var options = jobs.Get(name);
            Assert.AreEqual(cron, options.Cron, $"{name} has the wrong schedule");
            Assert.AreEqual("UTC", options.TimeZoneId, $"{name} must read its cron in UTC");
        }
    }

    [TestMethod]
    public void Given_SchedulerRegistration_When_JobsListed_Then_ExactlyTheExpectedJobsAreRegistered()
    {
        using var provider = BuildSchedulerServices();

        // Each AddJob binds Scheduling:Jobs:{name} once, so the named change-token sources list the registered jobs.
        var registered = provider.GetServices<IOptionsChangeTokenSource<ScheduledJobOptions>>()
            .Select(s => s.Name!)
            .Order(StringComparer.Ordinal)
            .ToList();

        CollectionAssert.AreEqual(ExpectedJobs.Keys.Order(StringComparer.Ordinal).ToList(), registered);
    }

    [TestMethod]
    public void Given_ShippedSchedulerSettings_When_Validated_Then_TheWorkerOptionsAreValid()
    {
        using var provider = BuildSchedulerServices();

        // Value runs the options validator against every registered job's timeout and the lease.
        var options = provider.GetRequiredService<IOptions<SchedulerOptions>>().Value;

        Assert.AreEqual(5, options.Slots);
        Assert.AreEqual(TimeSpan.FromDays(1), provider.GetRequiredService<IOptions<SchedulerRetentionSettings>>().Value.RunRetention);
    }

    private static ServiceProvider BuildSchedulerServices()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        builder.Configuration
            .AddJsonFile(RepoRoot.Combine("src", "Host", "TaskFlow.Scheduler", "appsettings.json"), optional: false)
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Never opened: the registration only needs a connection string to build the context options.
                [$"ConnectionStrings:{RegisterSchedulerServices.SchedulerConnectionName}"] =
                    "Host=localhost;Database=scheduler-registration-test;Username=test;Password=test"
            });
        builder.Services.AddSingleton<IConfiguration>(builder.Configuration);
        builder.AddJobScheduler();
        return builder.Services.BuildServiceProvider();
    }
}
