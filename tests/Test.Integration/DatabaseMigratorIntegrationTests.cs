using EF.Data.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data;
using TaskFlow.Infrastructure.Data;
using Test.Integration.Infrastructure;

namespace Test.Integration;

[TestClass]
[TestCategory("Integration")]
public sealed class DatabaseMigratorIntegrationTests
{
    [TestInitialize]
    public void TestSetup()
    {
        IntegrationTestSetup.AssertAvailable("SQL", SqlContainerFixture.StartupError);
    }

    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task DatabaseMigrator_AppliesAllTargets_AndIsIdempotent()
    {
        var connectionString = await SqlContainerFixture.CreateEmptyDatabaseConnectionStringAsync("TaskFlowMigrator");
        var runner = CreateRunner(connectionString);

        await runner.RunAsync(TestContext.CancellationToken);
        await runner.RunAsync(TestContext.CancellationToken);

        await using var trxn = SqlContainerFixture.CreateTrxnContext(connectionString);
        var taskFlowTableCount = await CountTablesAsync(trxn, "taskflow");
        Assert.IsGreaterThanOrEqualTo(7, taskFlowTableCount, $"Expected at least 7 taskflow tables, found {taskFlowTableCount}.");
        Assert.IsTrue(await TableExistsAsync(
            trxn,
            TaskFlowDbContextBase.SchemaName,
            TaskFlowDbContextBase.MigrationHistoryTable));
        Assert.IsFalse(await TableExistsAsync(
            trxn,
            "dbo",
            TaskFlowDbContextBase.MigrationHistoryTable));

        await using var flowEngine = SqlContainerFixture.CreateFlowEngineContext(connectionString);
        var flowEngineTableCount = await CountTablesAsync(flowEngine, TaskFlowFlowEngineDbContext.SchemaName);
        Assert.IsGreaterThanOrEqualTo(4, flowEngineTableCount, $"Expected at least 4 FlowEngine tables, found {flowEngineTableCount}.");
        Assert.IsTrue(await TableExistsAsync(
            flowEngine,
            TaskFlowFlowEngineDbContext.SchemaName,
            TaskFlowFlowEngineDbContext.MigrationHistoryTable));

        await using var tickerQ = SqlContainerFixture.CreateTickerQContext(connectionString);
        Assert.IsTrue(await TaskFlowTickerQSchemaValidator.SchemaExistsAsync(tickerQ, TestContext.CancellationToken));
        Assert.IsTrue(await TableExistsAsync(
            tickerQ,
            TaskFlowTickerQDbContext.SchemaName,
            TaskFlowTickerQDbContext.MigrationHistoryTable));
        Assert.AreEqual(42, await ExecuteScalarIntAsync(
            tickerQ,
            "SELECT [Value] FROM [Scheduler].[MigrationStepProof] WHERE [Id] = 1"));
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task TickerQValidation_FailsWhenSchemaMissing()
    {
        var connectionString = await SqlContainerFixture.CreateEmptyDatabaseConnectionStringAsync("TickerQMissing");
        await using var tickerQ = SqlContainerFixture.CreateTickerQContext(connectionString);

        Assert.IsTrue(await tickerQ.Database.CanConnectAsync(TestContext.CancellationToken));
        Assert.IsFalse(await TaskFlowTickerQSchemaValidator.SchemaExistsAsync(tickerQ, TestContext.CancellationToken));
    }

    private static DatabaseMigrationRunner CreateRunner(string connectionString)
    {
        return new DatabaseMigrationRunner(
        [
            new EntityFrameworkMigrationTarget<TaskFlowDbContextTrxn>(
                "TaskFlowDbContextTrxn",
                10,
                new TestDbContextFactory<TaskFlowDbContextTrxn>(() => SqlContainerFixture.CreateTrxnContext(connectionString)),
                [],
                NullLogger<EntityFrameworkMigrationTarget<TaskFlowDbContextTrxn>>.Instance),
            new EntityFrameworkMigrationTarget<TaskFlowFlowEngineDbContext>(
                "TaskFlowFlowEngineDbContext",
                20,
                new TestDbContextFactory<TaskFlowFlowEngineDbContext>(() => SqlContainerFixture.CreateFlowEngineContext(connectionString)),
                [],
                NullLogger<EntityFrameworkMigrationTarget<TaskFlowFlowEngineDbContext>>.Instance),
            new EntityFrameworkMigrationTarget<TaskFlowTickerQDbContext>(
                "TaskFlowTickerQDbContext",
                30,
                new TestDbContextFactory<TaskFlowTickerQDbContext>(() => SqlContainerFixture.CreateTickerQContext(connectionString)),
                CreateTickerQDataMigrationSteps(),
                NullLogger<EntityFrameworkMigrationTarget<TaskFlowTickerQDbContext>>.Instance)
        ],
        NullLogger<DatabaseMigrationRunner>.Instance);
    }

    private static IReadOnlyList<IDatabaseMigrationStep<TaskFlowTickerQDbContext>> CreateTickerQDataMigrationSteps() =>
    [
        new SqlDatabaseMigrationStep<TaskFlowTickerQDbContext>(
            "prepare-scratch-data",
            DatabaseMigrationStepPhase.BeforeSchema,
            10,
            """
IF OBJECT_ID(N'tempdb..#TaskFlowMigrationScratch', N'U') IS NOT NULL
BEGIN
    DROP TABLE #TaskFlowMigrationScratch;
END;

CREATE TABLE #TaskFlowMigrationScratch
(
    [Id] int NOT NULL PRIMARY KEY,
    [Value] int NOT NULL
);

INSERT INTO #TaskFlowMigrationScratch ([Id], [Value]) VALUES (1, 42);
"""),
        new SqlDatabaseMigrationStep<TaskFlowTickerQDbContext>(
            "apply-scratch-data",
            DatabaseMigrationStepPhase.AfterSchema,
            10,
            """
IF OBJECT_ID(N'[Scheduler].[MigrationStepProof]', N'U') IS NULL
BEGIN
    CREATE TABLE [Scheduler].[MigrationStepProof]
    (
        [Id] int NOT NULL PRIMARY KEY,
        [Value] int NOT NULL
    );
END;

MERGE [Scheduler].[MigrationStepProof] AS target
USING (SELECT [Id], [Value] FROM #TaskFlowMigrationScratch) AS source
    ON target.[Id] = source.[Id]
WHEN MATCHED THEN
    UPDATE SET [Value] = source.[Value]
WHEN NOT MATCHED THEN
    INSERT ([Id], [Value]) VALUES (source.[Id], source.[Value]);
""")
    ];

    private static async Task<int> CountTablesAsync(DbContext db, string schema)
    {
        return await ExecuteScalarIntAsync(
            db,
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @schema",
            ("@schema", schema));
    }

    private static async Task<bool> TableExistsAsync(DbContext db, string schema, string table)
    {
        var count = await ExecuteScalarIntAsync(
            db,
            """
SELECT COUNT(*)
FROM sys.tables t
JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE s.name = @schema AND t.name = @table
""",
            ("@schema", schema),
            ("@table", table));

        return count == 1;
    }

    private static async Task<int> ExecuteScalarIntAsync(
        DbContext db,
        string commandText,
        params (string Name, object Value)[] parameters)
    {
        var connection = db.Database.GetDbConnection();
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection)
        {
            await db.Database.OpenConnectionAsync();
        }

        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = commandText;

            foreach (var (name, value) in parameters)
            {
                var parameter = command.CreateParameter();
                parameter.ParameterName = name;
                parameter.Value = value;
                command.Parameters.Add(parameter);
            }

            var result = await command.ExecuteScalarAsync();
            return Convert.ToInt32(result);
        }
        finally
        {
            if (closeConnection)
            {
                await db.Database.CloseConnectionAsync();
            }
        }
    }

    private sealed class TestDbContextFactory<TContext>(Func<TContext> create) : IDbContextFactory<TContext>
        where TContext : DbContext
    {
        public TContext CreateDbContext() => create();

        public Task<TContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(create());
    }

    public TestContext TestContext { get; set; } = null!;
}
