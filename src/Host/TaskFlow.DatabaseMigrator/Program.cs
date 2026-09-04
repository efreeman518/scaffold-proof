using EF.Data.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;

var builder = Host.CreateApplicationBuilder(args);

builder.AddServiceDefaults();
builder.Services
    .AddDatabaseMigrationRunner()
    .AddTaskFlowMigrationDbContexts(builder.Configuration)
    // Order is intentional: app schema first, FlowEngine second, Scheduler/TickerQ last.
    // Each target keeps its own migration history table even when local Aspire shares taskflowdb.
    .AddEfCoreMigrationTarget<TaskFlowDbContextTrxn>("TaskFlowDbContextTrxn", 10)
    .AddEfCoreMigrationTarget<TaskFlowFlowEngineDbContext>("TaskFlowFlowEngineDbContext", 20)
    .AddEfCoreMigrationTarget<TaskFlowTickerQDbContext>("TaskFlowTickerQDbContext", 30);

using var host = builder.Build();
using var scope = host.Services.CreateScope();
var runner = scope.ServiceProvider.GetRequiredService<DatabaseMigrationRunner>();
await runner.RunAsync();

static class DatabaseMigratorRegistration
{
    public static IServiceCollection AddTaskFlowMigrationDbContexts(
        this IServiceCollection services,
        IConfiguration config)
    {
        var trxn = RequireConnectionString(config, "TaskFlowDbContextTrxn");
        // FlowEngine and TickerQ can share the same physical database locally, but they keep
        // distinct logical connection names so Azure can split them later through configuration.
        var flowEngine = config.GetConnectionString("TaskFlowFlowEngineDbContext") ?? trxn;
        var tickerQ = RequireConnectionString(config, "TickerQDbContext");
        var commandTimeoutSeconds = config.GetValue<int?>("Database:MigrationCommandTimeoutSeconds") ?? 1800;

        services.AddDbContextFactory<TaskFlowDbContextTrxn>(options =>
            options.UseTaskFlowProvider(TaskFlowProviderOptions.FromConfiguration(
                config, trxn, TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName, commandTimeoutSeconds)));

        services.AddDbContextFactory<TaskFlowFlowEngineDbContext>(options =>
            options.UseTaskFlowProvider(TaskFlowProviderOptions.FromConfiguration(
                config, flowEngine, TaskFlowFlowEngineDbContext.MigrationHistoryTable, TaskFlowFlowEngineDbContext.SchemaName, commandTimeoutSeconds)));

        services.AddDbContextFactory<TaskFlowTickerQDbContext>(options =>
            options.UseTaskFlowProvider(TaskFlowProviderOptions.FromConfiguration(
                config, tickerQ, TaskFlowTickerQDbContext.MigrationHistoryTable, TaskFlowTickerQDbContext.SchemaName, commandTimeoutSeconds)));

        return services;
    }

    private static string RequireConnectionString(IConfiguration config, string name)
    {
        return config.GetConnectionString(name)
            ?? throw new InvalidOperationException($"Connection string '{name}' is required.");
    }
}
