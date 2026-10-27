using EF.BackgroundServices;
using EF.Data.Outbox;
using EF.Data.Scheduling;
using EF.Messaging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TaskFlow.Infrastructure.AI;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;
using TaskFlow.Infrastructure.Messaging.RabbitMq;
using TaskFlow.Scheduler.Handlers;
using TaskFlow.Scheduler.Handlers.Retention;
using TaskFlow.Bootstrapper;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Scheduler.Workers;

namespace TaskFlow.Scheduler;

/// <summary>
/// Scheduler composition. EF.Data.Scheduling owns scheduling mechanics (planning, claiming, retries, history);
/// TaskFlow handlers own the domain work each scheduled job runs.
/// </summary>
public static class RegisterSchedulerServices
{
    /// <summary>Configuration section the outbox dispatcher options bind from.</summary>
    public const string OutboxDispatcherConfigSection = "OutboxDispatcher";

    /// <summary>Connection string name of the scheduler database.</summary>
    public const string SchedulerConnectionName = "TaskFlowSchedulerDbContext";

    /// <summary>Job name of the scheduler's own run-history and parked-run retention sweep.</summary>
    public const string SchedulerRetentionJobName = "scheduler_retention";

    public static IServiceCollection AddSchedulerServices(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.AddOptions<ComplianceCheckSettings>()
            .Bind(config.GetSection(ComplianceCheckSettings.ConfigSectionName))
            .Validate(o => o.IsValid(), $"{ComplianceCheckSettings.ConfigSectionName}:WindowDays must be 1 to {ComplianceCheckSettings.MaxWindowDays}.")
            .ValidateOnStart();
        // Already added by the shared application registration; TryAdd keeps one meter per process.
        services.TryAddSingleton<MessagingMetrics>();

        // D-026: both drains run on every replica; the lease, not a leader election, keeps them apart.
        // EF.Data.Outbox owns the dispatcher, the claim and the settlement; the sections retune poll, lease, batch,
        // attempts (appsettings keeps MaxAttempts at 10; the package default is 5) and (D-055) the blob-delete
        // in-flight bound per environment. The host fails to start unless LeaseDuration > SendTimeout + SettlementTimeout.
        services.AddOptions<OutboxDispatcherOptions>()
            .Bind(config.GetSection(OutboxDispatcherConfigSection));
        services.AddOutboxDispatcher();

        services.AddOptions<BlobDeleteSettings>()
            .Bind(config.GetSection(BlobDeleteSettings.ConfigSectionName));
        services.AddLeasedWorkerService<BlobDeleteWorkerService, BlobDeleteSettings>();

        // D-034: the Scheduler is the RabbitMQ consumer host; the Functions runtime has no RabbitMQ trigger.
        if (RegisterServices.ResolveMessagingProvider(config) == MessagingProvider.RabbitMq)
        {
            // D-040: the embedding queue is declared and drained only on the PgVector arm.
            services.AddTaskFlowRabbitMqConsumers(
                config,
                includeEmbedding: AiServiceCollectionExtensions.ResolveSearchProvider(config) == SearchProvider.PgVector);
        }

        services.AddHealthChecks()
            // M13: Degraded past 60 s lag or 10k pending, Unhealthy past 300 s or 50k.
            .AddLeasedWorkBacklogCheck<OutboxMessage>("outbox", tags: ["ready", "full"])
            // Report-only: the blob-delete backlog is visible (and gauged) but never degrades readiness.
            .AddLeasedWorkBacklogCheck<BlobDeleteWork>("blobdelete", o =>
            {
                o.DegradedPending = o.UnhealthyPending = int.MaxValue;
                o.DegradedLag = o.UnhealthyLag = TimeSpan.MaxValue;
            }, "full");

        return services;
    }

    /// <summary>
    /// EF.Data.Scheduling over <see cref="TaskFlowSchedulerDbContext"/> (connection <c>TaskFlowSchedulerDbContext</c>,
    /// schema <c>scheduler</c>, migrated by TaskFlow.DatabaseMigrator). Every replica runs the cron planner and the job
    /// worker: each cron occurrence is enqueued once through a version-guarded state row and each run is claimed by one
    /// replica through a lease, so no lock service or leader election is needed. <c>Scheduling</c> retunes the worker
    /// (Slots, CronPollInterval, MaxAttempts, LeaseDuration) and <c>Scheduling:Jobs:{name}</c> one job's Cron, TimeZoneId
    /// or Timeout. Crons have six fields (seconds first) and read in UTC. Retention sweeps are staggered off the hour and
    /// off each other: they all delete, and running them together would concentrate the lock and log pressure they exist
    /// to spread out. The stall check (<c>Scheduling:Health:StallThreshold</c>, Degraded) reads the run history.
    /// </summary>
    public static IHostApplicationBuilder AddJobScheduler(this IHostApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var connStr = config.GetConnectionString(SchedulerConnectionName);
        if (string.IsNullOrWhiteSpace(connStr))
        {
            throw new InvalidOperationException($"Connection string '{SchedulerConnectionName}' is required.");
        }

        builder.Services.AddDbContext<TaskFlowSchedulerDbContext>(options => options.UseTaskFlowProvider(
            TaskFlowProviderOptions.FromConfiguration(
                config,
                connStr,
                TaskFlowSchedulerDbContext.MigrationHistoryTable,
                TaskFlowSchedulerDbContext.SchemaName)));

        builder.Services.AddEFScheduler<TaskFlowSchedulerDbContext>()
            .AddJob<OverdueTaskCheckHandler>(OverdueTaskCheckHandler.JobName, o => o.Cron = "0 0 */6 * * *")
            .AddJob<RecurringTaskGenerationHandler>(RecurringTaskGenerationHandler.JobName, o => o.Cron = "0 0 2 * * *")
            .AddJob<StaleTaskCleanupHandler>(StaleTaskCleanupHandler.JobName, o => o.Cron = "0 0 3 * * 0")
            // D-075: at 06:10, off the 06:00 overdue check.
            .AddJob<ComplianceCheckHandler>(ComplianceCheckHandler.JobName, o => o.Cron = "0 10 6 * * *")
            .AddJob<OutboxRetentionHandler>(OutboxRetentionHandler.JobName, o => o.Cron = "0 15 * * * *")
            .AddJob<ConsumerInboxRetentionHandler>(ConsumerInboxRetentionHandler.JobName, o => o.Cron = "0 20 * * * *")
            // Run history past Scheduling:Retention:RunRetention and parked runs past ParkedRetention; live rows stay.
            .AddJob<SchedulerRetentionHandler<TaskFlowSchedulerDbContext>>(SchedulerRetentionJobName, o => o.Cron = "0 30 4 * * *")
            .AddJob<AuditRetentionHandler>(AuditRetentionHandler.JobName, o => o.Cron = "0 40 4 * * *");

        builder.Services.AddHealthChecks()
            .AddSchedulerHealthCheck<TaskFlowSchedulerDbContext>(tags: ["ready", "memory"]);

        return builder;
    }

    /// <summary>
    /// Scheduler is a runtime host, not a migration owner: a missing schema means the deployment skipped
    /// TaskFlow.DatabaseMigrator or pointed TaskFlowSchedulerDbContext at the wrong database. The EF.Data.Scheduling
    /// validator names every missing table and never changes schema.
    /// </summary>
    public static async Task ValidateSchedulerDatabase(this WebApplication app)
    {
        await SchedulerSchemaValidator.ValidateAsync<TaskFlowSchedulerDbContext>(app.Services);
        app.Logger.SchedulerSchemaValidated();
    }
}
