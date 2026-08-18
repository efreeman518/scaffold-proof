using Microsoft.EntityFrameworkCore;
using TaskFlow.Infrastructure.Data;

namespace Test.Unit.Infrastructure;

/// <summary>Guards every TaskFlow database model without changing tracked migrations.</summary>
[TestClass]
public sealed class MigrationModelContractTests
{
    private const string ConnectionString =
        "Server=localhost;Database=TaskFlowMigrationContract;User Id=sa;Password=NotARealPassword1!;TrustServerCertificate=true";

    [TestMethod]
    public void MigrationOwners_HaveNoPendingModelChanges()
    {
        using var transactional = CreateTransactionalContext();
        using var flowEngine = new TaskFlowFlowEngineDbContext(
            new DbContextOptionsBuilder<TaskFlowFlowEngineDbContext>()
                .UseSqlServer(ConnectionString, sql =>
                {
                    sql.UseLatestCompatibilityLevel();
                    sql.MigrationsHistoryTable(
                        TaskFlowFlowEngineDbContext.MigrationHistoryTable,
                        TaskFlowFlowEngineDbContext.SchemaName);
                })
                .Options);
        using var tickerQ = new TaskFlowTickerQDbContext(
            new DbContextOptionsBuilder<TaskFlowTickerQDbContext>()
                .UseSqlServer(ConnectionString, sql =>
                {
                    sql.UseLatestCompatibilityLevel();
                    sql.MigrationsAssembly(typeof(TaskFlowTickerQDbContext).Assembly.GetName().Name);
                    sql.MigrationsHistoryTable(
                        TaskFlowTickerQDbContext.MigrationHistoryTable,
                        TaskFlowTickerQDbContext.SchemaName);
                })
                .Options);

        Assert.IsFalse(transactional.Database.HasPendingModelChanges(), nameof(TaskFlowDbContextTrxn));
        Assert.IsFalse(flowEngine.Database.HasPendingModelChanges(), nameof(TaskFlowFlowEngineDbContext));
        Assert.IsFalse(tickerQ.Database.HasPendingModelChanges(), nameof(TaskFlowTickerQDbContext));
    }

    [TestMethod]
    public void QueryContext_SharesTransactionalMigrationModel()
    {
        using var transactional = CreateTransactionalContext();
        using var query = new TaskFlowDbContextQuery(
            new DbContextOptionsBuilder<TaskFlowDbContextQuery>()
                .UseSqlServer(ConnectionString, ConfigurePrimarySql)
                .Options)
        {
            AuditId = "MigrationModelContract",
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001")
        };

        Assert.AreEqual(
            transactional.Database.GenerateCreateScript(),
            query.Database.GenerateCreateScript(),
            "The query context is covered by the transactional migration chain and must keep the same relational model.");
    }

    private static TaskFlowDbContextTrxn CreateTransactionalContext() =>
        new(new DbContextOptionsBuilder<TaskFlowDbContextTrxn>()
            .UseSqlServer(ConnectionString, ConfigurePrimarySql)
            .Options)
        {
            AuditId = "MigrationModelContract",
            TenantId = Guid.Parse("00000000-0000-0000-0000-000000000001")
        };

    private static void ConfigurePrimarySql(Microsoft.EntityFrameworkCore.Infrastructure.SqlServerDbContextOptionsBuilder sql)
    {
        sql.UseLatestCompatibilityLevel();
        sql.MigrationsHistoryTable(
            TaskFlowDbContextBase.MigrationHistoryTable,
            TaskFlowDbContextBase.SchemaName);
    }
}
