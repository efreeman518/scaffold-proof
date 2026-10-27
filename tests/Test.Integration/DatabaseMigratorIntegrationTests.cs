using EF.Data.Migrations;
using EF.IntegrationTesting.AspNetCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Data;
using TaskFlow.Infrastructure.Data;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// Runs the three migration targets in migrator order against an empty database on the lane's provider and
/// proves idempotency, per-target history tables, the scheduler schema check, and BeforeSchema/AfterSchema
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
        var connectionString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("TaskFlowMigrator", TestContext.CancellationToken);
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

        await using var scheduler = DbContainerFixture.CreateSchedulerContext(connectionString);
        Assert.IsEmpty(await scheduler.GetMissingTablesAsync(TestContext.CancellationToken));
        Assert.IsTrue(await TableExistsAsync(scheduler, TaskFlowSchedulerDbContext.SchemaName, TaskFlowSchedulerDbContext.MigrationHistoryTable));

        // The AfterSchema data step ran once per RunAsync and stayed idempotent (BeforeSchema clears, AfterSchema inserts).
        var proofRows = await trxn.ConsumerInbox.Where(x => x.Consumer == ProofConsumer).ToListAsync(TestContext.CancellationToken);
        Assert.HasCount(1, proofRows);
        Assert.AreEqual(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), proofRows[0].CompletedAtUtc);
    }

    [TestMethod]
    [Timeout(120000, CooperativeCancellation = true)]
    public async Task SchedulerValidation_FailsWhenSchemaMissing()
    {
        var connectionString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("SchedulerMissing", TestContext.CancellationToken);
        await using var scheduler = DbContainerFixture.CreateSchedulerContext(connectionString);

        Assert.IsTrue(await scheduler.Database.CanConnectAsync(TestContext.CancellationToken));
        Assert.IsNotEmpty(await scheduler.GetMissingTablesAsync(TestContext.CancellationToken));
    }

    private static DatabaseMigrationRunner CreateRunner(string connectionString)
    {
        return new DatabaseMigrationRunner(
        [
            new EntityFrameworkMigrationTarget<TaskFlowDbContextTrxn>(
                "TaskFlowDbContextTrxn",
                10,
                new EfTestDbContextFactory<TaskFlowDbContextTrxn>(() => DbContainerFixture.CreateTrxnContext(connectionString)),
                [],
                NullLogger<EntityFrameworkMigrationTarget<TaskFlowDbContextTrxn>>.Instance),
            new EntityFrameworkMigrationTarget<TaskFlowFlowEngineDbContext>(
                "TaskFlowFlowEngineDbContext",
                20,
                new EfTestDbContextFactory<TaskFlowFlowEngineDbContext>(() => DbContainerFixture.CreateFlowEngineContext(connectionString)),
                [],
                NullLogger<EntityFrameworkMigrationTarget<TaskFlowFlowEngineDbContext>>.Instance),
            new EntityFrameworkMigrationTarget<TaskFlowSchedulerDbContext>(
                "TaskFlowSchedulerDbContext",
                30,
                new EfTestDbContextFactory<TaskFlowSchedulerDbContext>(() => DbContainerFixture.CreateSchedulerContext(connectionString)),
                CreateSchedulerDataMigrationSteps(),
                NullLogger<EntityFrameworkMigrationTarget<TaskFlowSchedulerDbContext>>.Instance)
        ],
        NullLogger<DatabaseMigrationRunner>.Instance);
    }

    // Data steps target the EF-created taskflow."ConsumerInbox" (migrated by the order-10 target) with plain
    // DML and double-quoted identifiers, which both providers accept.
    private static IReadOnlyList<IDatabaseMigrationStep<TaskFlowSchedulerDbContext>> CreateSchedulerDataMigrationSteps() =>
    [
        new SqlDatabaseMigrationStep<TaskFlowSchedulerDbContext>(
            "clear-proof-row",
            DatabaseMigrationStepPhase.BeforeSchema,
            10,
            $"""DELETE FROM taskflow."ConsumerInbox" WHERE "Consumer" = '{ProofConsumer}'"""),
        new SqlDatabaseMigrationStep<TaskFlowSchedulerDbContext>(
            "insert-proof-row",
            DatabaseMigrationStepPhase.AfterSchema,
            10,
            $"""
            INSERT INTO taskflow."ConsumerInbox" ("Consumer", "MessageId", "ClaimToken", "ClaimedAtUtc", "CompletedAtUtc")
            VALUES ('{ProofConsumer}', '00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000000002',
                    '2026-01-01T00:00:00+00:00', '2026-01-01T00:00:00+00:00')
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

    public TestContext TestContext { get; set; } = null!;
}
