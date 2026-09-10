using EF.BackgroundServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TaskFlow.Infrastructure.AI;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;
using TaskFlow.Infrastructure.Messaging.RabbitMq;
using TaskFlow.Scheduler.Handlers;
using TaskFlow.Scheduler.Handlers.Retention;
using TaskFlow.Scheduler.Infrastructure;
using TaskFlow.Scheduler.Jobs;
using TaskFlow.Bootstrapper;
using TaskFlow.Observability.Meters;
using TaskFlow.Scheduler.Telemetry;
using TaskFlow.Scheduler.Workers;
using TickerQ.Dashboard.DependencyInjection;
using TickerQ.DependencyInjection;
using TickerQ.EntityFrameworkCore.DependencyInjection;
using TickerQ.Utilities.Entities;
using TickerQ.Utilities.Interfaces.Managers;

namespace TaskFlow.Scheduler;

/// <summary>
/// Scheduler composition and operational-store setup. TickerQ owns scheduling mechanics;
/// TaskFlow handlers own domain work invoked by each cron function.
/// </summary>
public static class RegisterSchedulerServices
{
    public static IServiceCollection AddSchedulerServices(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.AddScoped<OverdueTaskCheckHandler>();
        services.AddScoped<RecurringTaskGenerationHandler>();
        services.AddScoped<StaleTaskCleanupHandler>();
        services.AddScoped<OutboxRetentionHandler>();
        services.AddScoped<ConsumerInboxRetentionHandler>();
        services.AddScoped<TickerQOccurrenceRetentionHandler>();
        services.AddScoped<AuditRetentionHandler>();
        services.AddScoped<TaskMaintenanceJobs>();
        services.AddSingleton<SchedulingMetrics>();
        services.AddSingleton<SchedulerJobMeter>();
        // Already added by the shared application registration; TryAdd keeps one meter per process.
        services.TryAddSingleton<MessagingMetrics>();

        // D-026: both drains run on every replica; the lease, not a leader election, keeps them apart.
        // EF.BackgroundServices owns the loop; the sections retune poll, lease, batch and (D-055) the
        // blob-delete in-flight bound per environment.
        services.AddOptions<OutboxDispatcherSettings>()
            .Bind(config.GetSection(OutboxDispatcherSettings.ConfigSectionName));
        services.AddLeasedWorkerService<OutboxDispatcherService, OutboxDispatcherSettings>();

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
            .AddCheck<SchedulerHealthCheck>("scheduler", tags: ["ready", "memory"])
            .AddCheck<OutboxHealthCheck>("outbox", tags: ["ready", "full"]);

        return services;
    }

    public static IHostApplicationBuilder AddTickerQConfig(this IHostApplicationBuilder builder)
    {
        var config = builder.Configuration;
        var maxConcurrency = config.GetValue("Scheduling:MaxConcurrency", Math.Max(1, Environment.ProcessorCount));
        var pollIntervalSeconds = config.GetValue("Scheduling:PollIntervalSeconds", 30);
        var usePersistence = config.GetValue("Scheduling:UsePersistence", true);

        builder.Services.AddTickerQ(options =>
        {
            options.SetExceptionHandler<TaskFlowSchedulerExceptionHandler>();

            options.ConfigureScheduler(scheduler =>
            {
                scheduler.MaxConcurrency = maxConcurrency;
                scheduler.SchedulerTimeZone = TimeZoneInfo.Utc;
                scheduler.IdleWorkerTimeOut = TimeSpan.FromMinutes(2);
                scheduler.FallbackIntervalChecker = TimeSpan.FromSeconds(pollIntervalSeconds);
                // Machine name alone collides when two replicas share a host (Aspire runs the Scheduler with
                // WithReplicas(2)); TickerQ leases cron occurrences by node identity, so two nodes claiming the
                // same name would each believe they hold the other's lease.
                scheduler.NodeIdentifier = $"{Environment.MachineName}:{Environment.ProcessId}";
            });

            if (usePersistence)
            {
                var connStr = config.GetConnectionString("TickerQDbContext");
                if (string.IsNullOrWhiteSpace(connStr))
                {
                    throw new InvalidOperationException("Connection string 'TickerQDbContext' is required.");
                }

                // shortcut: TickerQ keeps a scoped (non-pooled) context because UseTickerQDbContext only accepts
                // Action<DbContextOptionsBuilder>; upgrade path is an upstream factory/pooled overload in TickerQ.EntityFrameworkCore.
                options.AddOperationalStore(efOptions =>
                    efOptions.UseTickerQDbContext<TaskFlowTickerQDbContext>(
                        dbOptions => dbOptions.UseTaskFlowProvider(TaskFlowProviderOptions.FromConfiguration(
                            config,
                            connStr,
                            TaskFlowTickerQDbContext.MigrationHistoryTable,
                            TaskFlowTickerQDbContext.SchemaName)),
                        schema: TaskFlowTickerQDbContext.SchemaName));
            }

            var enableDashboard = config.GetValue("Scheduling:EnableDashboard", false);
            if (enableDashboard)
            {
                options.AddDashboard(dashboard =>
                {
                    dashboard.SetBasePath(config["Scheduling:Dashboard:BasePath"] ?? "/scheduler");

                    var username = config["Scheduling:Dashboard:Username"];
                    var password = config["Scheduling:Dashboard:Password"];
                    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password))
                    {
                        throw new InvalidOperationException(
                            "TickerQ dashboard requires Scheduling:Dashboard:Username and Scheduling:Dashboard:Password.");
                    }

                    dashboard.WithBasicAuth(username, password);
                });
            }
        });

        return builder;
    }

    public static async Task ValidateTickerQDatabase(this WebApplication app)
    {
        var config = app.Configuration;
        var logger = app.Logger;
        var usePersistence = config.GetValue("Scheduling:UsePersistence", true);
        if (!usePersistence)
        {
            logger.TickerQPersistenceDisabled();
            return;
        }

        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<TaskFlowTickerQDbContext>();

        // Scheduler is a runtime host, not a migration owner. Missing schema means the
        // deployment skipped TaskFlow.DatabaseMigrator or pointed TickerQDbContext at the wrong database.
        if (!await db.Database.CanConnectAsync())
        {
            throw new InvalidOperationException("Cannot connect TickerQ operational store database.");
        }

        if (!await TaskFlowTickerQSchemaValidator.SchemaExistsAsync(db))
        {
            throw new InvalidOperationException(
                "TickerQ schema is missing or incomplete. Run TaskFlow.DatabaseMigrator before starting Scheduler.");
        }

        logger.TickerQSchemaValidated();
    }

    public static async Task SeedCronJobs(this WebApplication app)
    {
        using var scope = app.Services.CreateScope();
        var cronManager = scope.ServiceProvider.GetService<ICronTickerManager<CronTickerEntity>>();
        if (cronManager is null)
        {
            app.Logger.TickerQCronManagerUnavailable();
            return;
        }

        // Retention sweeps are staggered off the hour and off each other: they all delete, and running them
        // together would concentrate the lock and log pressure they exist to spread out.
        (string Function, string Expression)[] jobs =
        [
            (OverdueTaskCheckHandler.JobName, "0 0 */6 * * *"),
            (RecurringTaskGenerationHandler.JobName, "0 0 2 * * *"),
            (StaleTaskCleanupHandler.JobName, "0 0 3 * * 0"),
            (OutboxRetentionHandler.JobName, "0 15 * * * *"),
            (ConsumerInboxRetentionHandler.JobName, "0 20 * * * *"),
            (TickerQOccurrenceRetentionHandler.JobName, "0 30 4 * * *"),
            (AuditRetentionHandler.JobName, "0 40 4 * * *")
        ];

        foreach (var (function, expression) in jobs)
        {
            await cronManager.AddAsync(new CronTickerEntity
            {
                Function = function,
                Expression = expression
            });
        }

        app.Logger.TickerQCronJobsSeeded();
    }
}
