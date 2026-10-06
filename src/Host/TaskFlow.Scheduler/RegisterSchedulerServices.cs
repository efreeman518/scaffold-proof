using EF.BackgroundServices;
using EF.BackgroundServices.TickerQ;
using EF.Data.Outbox;
using EF.Messaging;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TaskFlow.Infrastructure.AI;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;
using TaskFlow.Infrastructure.Messaging.RabbitMq;
using TaskFlow.Scheduler.Handlers;
using TaskFlow.Scheduler.Handlers.Retention;
using TaskFlow.Scheduler.Jobs;
using TaskFlow.Bootstrapper;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Scheduler.Workers;
using TickerQ.Dashboard.DependencyInjection;
using TickerQ.DependencyInjection;
using TickerQ.Utilities;
using TickerQ.Utilities.Entities;

namespace TaskFlow.Scheduler;

/// <summary>
/// Scheduler composition and operational-store setup. TickerQ owns scheduling mechanics;
/// TaskFlow handlers own domain work invoked by each cron function.
/// </summary>
public static class RegisterSchedulerServices
{
    /// <summary>Configuration section the outbox dispatcher options bind from.</summary>
    public const string OutboxDispatcherConfigSection = "OutboxDispatcher";

    public static IServiceCollection AddSchedulerServices(
        this IServiceCollection services,
        IConfiguration config)
    {
        services.AddScoped<OverdueTaskCheckHandler>();
        services.AddScoped<RecurringTaskGenerationHandler>();
        services.AddScoped<StaleTaskCleanupHandler>();
        services.AddScoped<OutboxRetentionHandler>();
        services.AddScoped<ConsumerInboxRetentionHandler>();
        services.AddScoped<AuditRetentionHandler>();
        services.AddScoped<ComplianceCheckHandler>();
        services.AddOptions<ComplianceCheckSettings>()
            .Bind(config.GetSection(ComplianceCheckSettings.ConfigSectionName))
            .Validate(o => o.IsValid(), $"{ComplianceCheckSettings.ConfigSectionName}:WindowDays must be 1 to {ComplianceCheckSettings.MaxWindowDays}.")
            .ValidateOnStart();
        services.AddScoped<TaskMaintenanceJobs>();
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
    /// TickerQ through EF.BackgroundServices.TickerQ (S14/S15): <c>AddEFTickerQ</c> binds <c>Scheduling</c>
    /// (MaxConcurrency, PollIntervalSeconds; UTC and the <c>{MachineName}:{ProcessId}</c> node identity, because
    /// Aspire runs two Scheduler replicas on one host), registers <see cref="ScheduledJobRunner"/> and its telemetry,
    /// and, with the operational store, seeds the <c>[TickerFunction]</c> crons inside the distributed seed lock
    /// (EF.Cache's <c>IDistributedLock</c>: Redis, or in-process on a single replica) and registers the package
    /// occurrence retention handler. The stall check (<c>Scheduling:Health:StallThreshold</c>, Degraded) needs the
    /// store, so it is registered with it. The host must also call <c>UseTickerQ()</c>.
    /// </summary>
    public static IHostApplicationBuilder AddTickerQConfig(this IHostApplicationBuilder builder)
    {
        var config = builder.Configuration;

        if (!config.GetValue("Scheduling:UsePersistence", true))
        {
            builder.Services.AddEFTickerQ(config, options => AddDashboard(options, config));
            return builder;
        }

        var connStr = config.GetConnectionString("TickerQDbContext");
        if (string.IsNullOrWhiteSpace(connStr))
        {
            throw new InvalidOperationException("Connection string 'TickerQDbContext' is required.");
        }

        // shortcut: TickerQ keeps a scoped (non-pooled) context because UseTickerQDbContext only accepts
        // Action<DbContextOptionsBuilder>; upgrade path is an upstream factory/pooled overload in TickerQ.EntityFrameworkCore.
        builder.Services.AddEFTickerQ<TaskFlowTickerQDbContext>(
            config,
            dbOptions => dbOptions.UseTaskFlowProvider(TaskFlowProviderOptions.FromConfiguration(
                config,
                connStr,
                TaskFlowTickerQDbContext.MigrationHistoryTable,
                TaskFlowTickerQDbContext.SchemaName)),
            TaskFlowTickerQDbContext.SchemaName,
            options => AddDashboard(options, config));
        builder.Services.AddHealthChecks()
            .AddSchedulerHealthCheck<TaskFlowTickerQDbContext>(tags: ["ready", "memory"]);

        return builder;
    }

    /// <summary>The TickerQ dashboard, only when <c>Scheduling:EnableDashboard</c> is set, and never without basic auth.</summary>
    private static void AddDashboard(TickerOptionsBuilder<TimeTickerEntity, CronTickerEntity> options, IConfiguration config)
    {
        if (!config.GetValue("Scheduling:EnableDashboard", false))
            return;

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

    /// <summary>
    /// Scheduler is a runtime host, not a migration owner: a missing schema means the deployment skipped
    /// TaskFlow.DatabaseMigrator or pointed TickerQDbContext at the wrong database. The EF.BackgroundServices.TickerQ
    /// validator names every missing table and never changes schema.
    /// </summary>
    public static async Task ValidateTickerQDatabase(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Scheduling:UsePersistence", true))
        {
            app.Logger.TickerQPersistenceDisabled();
            return;
        }

        await TickerQSchemaValidator.ValidateAsync<TaskFlowTickerQDbContext>(app.Services);
        app.Logger.TickerQSchemaValidated();
    }
}
