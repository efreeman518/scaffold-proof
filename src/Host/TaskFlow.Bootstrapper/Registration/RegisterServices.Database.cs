using EF.Data;
using EF.Data.Contracts;
using EF.Data.Encryption;
using EF.Data.Interceptors;
using EF.Data.Outbox;
using EF.BackgroundServices.InternalMessageBus;
using EF.Common.Contracts;
using EF.Messaging.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Messaging;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;
using TaskFlow.Infrastructure.Repositories;

namespace TaskFlow.Bootstrapper;

/// <summary>Configures database services for TaskFlow runtime hosts.</summary>
public static partial class RegisterServices
{
    /// <summary>
    /// The EF.Data tenant query filter fails closed: a context with no tenant reads no tenant rows unless its
    /// scope is marked all-tenants. A caller with no tenant reads every tenant only when it is the system
    /// identity (message consumers, scheduled jobs and other no-request work) or a global admin. A caller that
    /// carries a tenant stays pinned to it, global admin included; a tenant-less caller with neither role reads
    /// nothing.
    /// </summary>
    internal static bool AllowsAllTenants(IRequestContext<string, Guid?> requestContext) =>
        requestContext.TenantId is null
        && (requestContext.RoleExists(AppConstants.ROLE_SYSTEM) || requestContext.RoleExists(AppConstants.ROLE_GLOBAL_ADMIN));

    /// <summary>
    /// Registers write DbContext, read DbContext, FlowEngine DbContext, and repositories.
    /// Provider selection (SQL Server / PostgreSQL) happens once in <see cref="TaskFlowDbProviderExtensions.UseTaskFlowProvider"/>.
    /// </summary>
    internal static void AddDatabaseServices(IServiceCollection services, IConfiguration config)
    {
        // AuditHandler persists through the internal bus. Do not let DI inject IAuditLogRepository into
        // the interceptor's optional direct-sink parameter: the NonAzure relational sink uses this same
        // context and would recurse while the pooled factory builds its options.
        services.AddTransient(sp => new AuditInterceptor<string, Guid?>(
            sp.GetRequiredService<IInternalMessageBus>(), []));
        // D-026/M10: EF.Data.Outbox stages raised domain events as outbox rows in the same SaveChanges as the domain
        // write (its singleton OutboxStagingInterceptor), through TaskFlow's mapper (envelope + TenantId header);
        // IOutboxStaging covers the jobs that have no tracked aggregate, and the leased work store claims both tables.
        services.AddSingleton<IOutboxEventMapper, TaskFlowOutboxEventMapper>();
        services.AddOutbox<TaskFlowDbContextTrxn>(o =>
        {
            o.DefaultDestination = TaskFlowIntegrationEvents.Destination;
            o.SerializerOptions = TaskFlowMessagingJsonContext.Default.Options;
        });
        // No ConnectionNoLockInterceptor registration: nothing ever added it to a context (D-004 keeps the
        // read isolation default on both providers), and EF.Data 1.1.100 marks it [Obsolete] in favor of
        // EF.Data.SqlServer (package request 3), so the dead line was the only obsolete usage in the tree.
        // D-023: one AES-GCM column encryptor per process, bound from Database:Encryption (fails fast without a key).
        services.AddColumnEncryption(config);

        var dbConnectionStringTrxn = config.GetConnectionString("TaskFlowDbContextTrxn") ?? "";
        // D-027: the Query context is an independently authored connection string on both providers.
        // SQL Server callers put `ApplicationIntent=ReadOnly` in it (Hyperscale HA secondary); PostgreSQL
        // callers point it at the read-replica FQDN. Nothing is appended here.
        var dbConnectionStringQuery = config.GetConnectionString("TaskFlowDbContextQuery") ?? "";
        var dbConnectionStringFlowEngine =
            config.GetConnectionString("TaskFlowFlowEngineDbContext") ?? dbConnectionStringTrxn;

        services.AddPooledDbContextFactory<TaskFlowDbContextTrxn>((sp, options) =>
        {
            UseTaskFlowProviderIfConfigured(options, config, dbConnectionStringTrxn,
                TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName);
            options.UseColumnEncryption(sp.GetRequiredService<IColumnEncryptor>());
            options.AddInterceptors(
                sp.GetRequiredService<AuditInterceptor<string, Guid?>>(),
                sp.GetRequiredService<OutboxStagingInterceptor>(),
                sp.GetRequiredService<BlindIndexInterceptor>());
        });
        services.AddScoped(sp => new DbContextScopedFactory<TaskFlowDbContextTrxn, string, Guid?>(
            sp.GetRequiredService<IDbContextFactory<TaskFlowDbContextTrxn>>(),
            sp.GetRequiredService<IRequestContext<string, Guid?>>(),
            sp.GetService<TimeProvider>(),
            AllowsAllTenants));
        services.AddScoped(sp => sp.GetRequiredService<DbContextScopedFactory<TaskFlowDbContextTrxn, string, Guid?>>()
            .CreateDbContext());

        services.AddPooledDbContextFactory<TaskFlowDbContextQuery>((sp, options) =>
        {
            options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
            UseTaskFlowProviderIfConfigured(options, config, dbConnectionStringQuery,
                TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName);
            options.UseColumnEncryption(sp.GetRequiredService<IColumnEncryptor>());
        });
        services.AddScoped(sp => new DbContextScopedFactory<TaskFlowDbContextQuery, string, Guid?>(
            sp.GetRequiredService<IDbContextFactory<TaskFlowDbContextQuery>>(),
            sp.GetRequiredService<IRequestContext<string, Guid?>>(),
            sp.GetService<TimeProvider>(),
            AllowsAllTenants));
        services.AddScoped(sp => sp.GetRequiredService<DbContextScopedFactory<TaskFlowDbContextQuery, string, Guid?>>()
            .CreateDbContext());

        services.AddPooledDbContextFactory<TaskFlowFlowEngineDbContext>((sp, options) =>
            UseTaskFlowProviderIfConfigured(options, config, dbConnectionStringFlowEngine,
                TaskFlowFlowEngineDbContext.MigrationHistoryTable, TaskFlowFlowEngineDbContext.SchemaName));

        services.AddScoped(typeof(IRepositoryTrxn<,>), typeof(TaskFlowRepositoryTrxn<,>));
        services.AddScoped(typeof(IRepositoryQuery<,>), typeof(TaskFlowRepositoryQuery<,>));

        services.AddScoped<ICategoryRepositoryTrxn, CategoryRepositoryTrxn>();
        services.AddScoped<ICategoryRepositoryQuery, CategoryRepositoryQuery>();
        services.AddScoped<ITaskItemRepositoryTrxn, TaskItemRepositoryTrxn>();
        services.AddScoped<ITaskItemRepositoryQuery, TaskItemRepositoryQuery>();
        services.AddScoped<IAttachmentRepositoryTrxn, AttachmentRepositoryTrxn>();
        // D-075: how long an upload's blob-delete reservation waits before it may delete content whose row never landed.
        services.AddOptions<AttachmentUploadSettings>()
            .Bind(config.GetSection(AttachmentUploadSettings.ConfigSectionName))
            .Validate(o => o.OrphanBlobGrace > TimeSpan.Zero, "AttachmentUpload:OrphanBlobGrace must be positive.")
            .ValidateOnStart();
        services.AddScoped<IAttachmentRepositoryQuery, AttachmentRepositoryQuery>();

        services.AddScoped<ITagRepositoryQuery, TagRepositoryQuery>();
        services.AddScoped<ICommentRepositoryQuery, CommentRepositoryQuery>();
        services.AddScoped<IChecklistItemRepositoryQuery, ChecklistItemRepositoryQuery>();

        // M12: two-state inbox; renewal takes short-lived contexts from the pooled factory registered above.
        services.AddInbox<TaskFlowDbContextTrxn>();
        // Cross-tenant system access for the scheduler jobs (IgnoreQueryFilters), so background work no
        // longer leans on the request context defaulting to global admin.
        services.AddScoped<ITaskItemSystemRepository, TaskItemSystemRepository>();
        // D-074: Idempotency-Key header mappings, saved on the write context ahead of the request's own write.
        services.AddScoped<IIdempotencyKeyRepository, IdempotencyKeyRepository>();
    }

    // An empty connection string leaves the context unconfigured so test hosts can replace it (InMemory).
    private static void UseTaskFlowProviderIfConfigured(
        DbContextOptionsBuilder options,
        IConfiguration config,
        string connectionString,
        string migrationsHistoryTable,
        string migrationsHistorySchema)
    {
        if (string.IsNullOrEmpty(connectionString)) return;

        options.UseTaskFlowProvider(TaskFlowProviderOptions.FromConfiguration(
            config, connectionString, migrationsHistoryTable, migrationsHistorySchema));
    }
}
