using Microsoft.EntityFrameworkCore;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;

namespace Test.Unit.Infrastructure;

/// <summary>
/// Guards every TaskFlow database model on both providers without touching a database: pending-model-change
/// detection and create-script generation need only the provider's type mapping and the migrations assembly.
/// </summary>
[TestClass]
public sealed class MigrationModelContractTests
{
    private const string SqlServerConnectionString =
        "Server=localhost;Database=TaskFlowMigrationContract;User Id=sa;Password=NotARealPassword1!;TrustServerCertificate=true";

    private const string PostgreSqlConnectionString =
        "Host=localhost;Database=TaskFlowMigrationContract;Username=postgres;Password=NotARealPassword1!";

    [TestMethod]
    [DataRow(TaskFlowDbProvider.SqlServer)]
    [DataRow(TaskFlowDbProvider.PostgreSql)]
    public void MigrationOwners_HaveNoPendingModelChanges(TaskFlowDbProvider provider)
    {
        using var transactional = CreateTransactionalContext(provider);
        using var flowEngine = new TaskFlowFlowEngineDbContext(Build<TaskFlowFlowEngineDbContext>(
            provider, TaskFlowFlowEngineDbContext.MigrationHistoryTable, TaskFlowFlowEngineDbContext.SchemaName));
        using var tickerQ = new TaskFlowTickerQDbContext(Build<TaskFlowTickerQDbContext>(
            provider, TaskFlowTickerQDbContext.MigrationHistoryTable, TaskFlowTickerQDbContext.SchemaName));

        Assert.IsFalse(transactional.Database.HasPendingModelChanges(), $"{nameof(TaskFlowDbContextTrxn)} on {provider}");
        Assert.IsFalse(flowEngine.Database.HasPendingModelChanges(), $"{nameof(TaskFlowFlowEngineDbContext)} on {provider}");
        Assert.IsFalse(tickerQ.Database.HasPendingModelChanges(), $"{nameof(TaskFlowTickerQDbContext)} on {provider}");
    }

    [TestMethod]
    [DataRow(TaskFlowDbProvider.SqlServer)]
    [DataRow(TaskFlowDbProvider.PostgreSql)]
    public void QueryContext_SharesTransactionalMigrationModel(TaskFlowDbProvider provider)
    {
        using var transactional = CreateTransactionalContext(provider);
        using var query = new TaskFlowDbContextQuery(Build<TaskFlowDbContextQuery>(
            provider, TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName))
        {
            AuditId = "MigrationModelContract",
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001")
        };

        Assert.AreEqual(
            transactional.Database.GenerateCreateScript(),
            query.Database.GenerateCreateScript(),
            $"The query context is covered by the transactional migration chain and must keep the same relational model on {provider}.");
    }

    private static TaskFlowDbContextTrxn CreateTransactionalContext(TaskFlowDbProvider provider) =>
        new(Build<TaskFlowDbContextTrxn>(provider, TaskFlowDbContextBase.MigrationHistoryTable, TaskFlowDbContextBase.SchemaName))
        {
            AuditId = "MigrationModelContract",
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001")
        };

    private static DbContextOptions<TContext> Build<TContext>(TaskFlowDbProvider provider, string historyTable, string historySchema)
        where TContext : DbContext =>
        new DbContextOptionsBuilder<TContext>()
            .UseTaskFlowProvider(new TaskFlowProviderOptions(
                provider,
                provider == TaskFlowDbProvider.SqlServer ? SqlServerConnectionString : PostgreSqlConnectionString,
                historyTable,
                historySchema))
            .Options;
}
