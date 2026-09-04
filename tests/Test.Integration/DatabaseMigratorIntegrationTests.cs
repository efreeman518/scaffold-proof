using EF.Data.Migrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data;
using TaskFlow.Infrastructure.Data;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// Runs the three migration targets in migrator order against an empty database on the lane's provider and
/// proves idempotency, per-target history tables, the TickerQ schema validator, and BeforeSchema/AfterSchema
/// data steps. Every SQL statement here is standard SQL (INFORMATION_SCHEMA, quoted identifiers) so the same
/// test runs on SQL Server and PostgreSQL.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public sealed class DatabaseMigratorIntegrationTests
{
    private const string ProofConsumer = "migration-step-proof";

    [TestInitialize]
    public void TestSetup()
    {
        IntegrationTestSetup.AssertAvailable("Database", DbContainerFixture.StartupError);
    }

    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task DatabaseMigrator_AppliesAllTargets_AndIsIdempotent()
    {
        var connectionString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("TaskFlowMigrator");
        var runner = CreateRunner(connectionString);

        await runner.RunAsync(TestContext.CancellationToken);
        await runner.RunAsync(TestContext.CancellationToken);

        await using var trxn = DbContainerFixture.CreateTrxnContext(connectionString);
        var taskFlowTableCount = await CountTablesAsync(trxn, TaskFlowDbContextBase.SchemaName);
        Assert.IsGreaterThanOrEqualTo(10, taskFlowTableCount, $"Expected at least 10 taskflow tables, found {taskFlowTableCount}.");
        Assert.IsTrue(await TableExistsAsync(trxn, TaskFlowDbContextBase.SchemaName, TaskFlowDbContextBase.MigrationHistoryTable));

        await using var flowEngine = DbContainerFixture.CreateFlowEngineContext(connectionString);
        var flowEngineTableCount = await CountTablesAsync(flowEngine, TaskFlowFlowEngineDbContext.SchemaName);
        Assert.IsGreaterThanOrEqualTo(4, flowEngineTableCount, $"Expected at least 4 FlowEngine tables, found {flowEngineTableCount}.");
        Assert.IsTrue(await TableExistsAsync(flowEngine, TaskFlowFlowEngineDbContext.SchemaName, TaskFlowFlowEngineDbContext.MigrationHistoryTable));

        await using var tickerQ = DbContainerFixture.CreateTickerQContext(connectionString);
        Assert.IsTrue(await TaskFlowTickerQSchemaValidator.SchemaExistsAsync(tickerQ, TestContext.CancellationToken));
        Assert.IsTrue(await TableExistsAsync(tickerQ, TaskFlowTickerQDbContext.SchemaName, TaskFlowTickerQDbContext.MigrationHistoryTable));

        // The AfterSchema data step ran once per RunAsync and stayed idempotent (BeforeSchema clears, AfterSchema inserts).
        var proofRows = await trxn.ConsumerInbox.Where(x => x.Consumer == ProofConsumer).ToListAsync(TestContext.CancellationToken);
        Assert.HasCount(1, proofRows);
        Assert.AreEqual(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), proofRows[0].ProcessedAtUtc);
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task TickerQValidation_FailsWhenSchemaMissing()
    {
        var connectionString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("TickerQMissing");
        await using var tickerQ = DbContainerFixture.CreateTickerQContext(connectionString);

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
                new TestDbContextFactory<TaskFlowDbContextTrxn>(() => DbContainerFixture.CreateTrxnContext(connectionString)),
                [],
                NullLogger<EntityFrameworkMigrationTarget<TaskFlowDbContextTrxn>>.Instance),
            new EntityFrameworkMigrationTarget<TaskFlowFlowEngineDbContext>(
                "TaskFlowFlowEngineDbContext",
                20,
                new TestDbContextFactory<TaskFlowFlowEngineDbContext>(() => DbContainerFixture.CreateFlowEngineContext(connectionString)),
                [],
                NullLogger<EntityFrameworkMigrationTarget<TaskFlowFlowEngineDbContext>>.Instance),
            new EntityFrameworkMigrationTarget<TaskFlowTickerQDbContext>(
                "TaskFlowTickerQDbContext",
                30,
                new TestDbContextFactory<TaskFlowTickerQDbContext>(() => DbContainerFixture.CreateTickerQContext(connectionString)),
                CreateTickerQDataMigrationSteps(),
                NullLogger<EntityFrameworkMigrationTarget<TaskFlowTickerQDbContext>>.Instance)
        ],
        NullLogger<DatabaseMigrationRunner>.Instance);
    }

    // Data steps target the EF-created taskflow."ConsumerInbox" (migrated by the order-10 target) with plain
    // DML and double-quoted identifiers, which both providers accept.
    private static IReadOnlyList<IDatabaseMigrationStep<TaskFlowTickerQDbContext>> CreateTickerQDataMigrationSteps() =>
    [
        new SqlDatabaseMigrationStep<TaskFlowTickerQDbContext>(
            "clear-proof-row",
            DatabaseMigrationStepPhase.BeforeSchema,
            10,
            $"""DELETE FROM taskflow."ConsumerInbox" WHERE "Consumer" = '{ProofConsumer}'"""),
        new SqlDatabaseMigrationStep<TaskFlowTickerQDbContext>(
            "insert-proof-row",
            DatabaseMigrationStepPhase.AfterSchema,
            10,
            $"""
            INSERT INTO taskflow."ConsumerInbox" ("Consumer", "MessageId", "ProcessedAtUtc")
            VALUES ('{ProofConsumer}', '00000000-0000-0000-0000-000000000001', '2026-01-01T00:00:00+00:00')
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
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.TABLES WHERE TABLE_SCHEMA = @schema AND TABLE_NAME = @table",
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
