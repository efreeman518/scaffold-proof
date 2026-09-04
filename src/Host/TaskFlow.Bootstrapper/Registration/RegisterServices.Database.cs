using EF.Data;
using EF.Data.Contracts;
using EF.Data.Interceptors;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TaskFlow.Application.Contracts.Repositories;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;
using TaskFlow.Infrastructure.Repositories;

namespace TaskFlow.Bootstrapper;

/// <summary>Configures database services for TaskFlow runtime hosts.</summary>
public static partial class RegisterServices
{
    /// <summary>
    /// Registers write DbContext, read DbContext, FlowEngine DbContext, and repositories.
    /// Provider selection (SQL Server / PostgreSQL) happens once in <see cref="TaskFlowDbProviderExtensions.UseTaskFlowProvider"/>.
    /// </summary>
    private static void AddDatabaseServices(IServiceCollection services, IConfiguration config)
    {
        services.AddTransient<AuditInterceptor<string, Guid?>>();
        services.AddTransient<ConnectionNoLockInterceptor>();

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
            var auditInterceptor = sp.GetRequiredService<AuditInterceptor<string, Guid?>>();
            options.AddInterceptors(auditInterceptor);
        });
        services.AddScoped<DbContextScopedFactory<TaskFlowDbContextTrxn, string, Guid?>>();
        services.AddScoped(sp => sp.GetRequiredService<DbContextScopedFactory<TaskFlowDbContextTrxn, string, Guid?>>()
            .CreateDbContext());

        services.AddPooledDbContextFactory<TaskFlowDbContextQuery>((sp, options) =>
        {
            options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
            UseTaskFlowProviderIfConfigured(options, config, dbConnectionStringQuery,
                TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName);
        });
        services.AddScoped<DbContextScopedFactory<TaskFlowDbContextQuery, string, Guid?>>();
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
        services.AddScoped<IAttachmentRepositoryQuery, AttachmentRepositoryQuery>();

        services.AddScoped<ITagRepositoryQuery, TagRepositoryQuery>();
        services.AddScoped<ICommentRepositoryQuery, CommentRepositoryQuery>();
        services.AddScoped<IChecklistItemRepositoryQuery, ChecklistItemRepositoryQuery>();
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
