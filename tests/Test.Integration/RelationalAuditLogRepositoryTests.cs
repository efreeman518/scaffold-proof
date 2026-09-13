using EF.Common.Contracts;
using EF.BackgroundServices.InternalMessageBus;
using EF.Data.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TaskFlow.Bootstrapper;
using TaskFlow.Application.MessageHandlers;
using TaskFlow.Domain.Model;
using TaskFlow.Domain.Shared;
using TaskFlow.Hosting;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Operational;
using TaskFlow.Infrastructure.Messaging.RabbitMq;
using TaskFlow.Infrastructure.Repositories;
using Test.Integration.Infrastructure;
using Test.Support;
using Test.Support.Hosting;

namespace Test.Integration;

/// <summary>
/// D-039 relational audit sink against a real database, on whichever provider the lane selected
/// (TASKFLOW_LANE). Mirrors <see cref="AuditLogRepositoryAzuriteTests"/> so the two arms are
/// held to the same contract: the tenant-first key, the sentinel tenant for entries with no tenant, the
/// round trip of audit metadata, and the retention sweep - here also proving the sweep really batches
/// instead of issuing one unbounded DELETE.
/// Component tier: contexts directly against the standalone database Testcontainer.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public class RelationalAuditLogRepositoryTests
{
    private const string SystemTenantId = "_system";
    private const int RetentionRowCount = 250;
    private const int RetentionBatchSize = 100;

    /// <summary>Marks the test Inconclusive when the database container failed to start.</summary>
    [TestInitialize]
    public void TestSetup()
    {
        IntegrationTestSetup.RequireLane(HostingLane.NonAzure);
        IntegrationTestSetup.AssertAvailable("PostgreSQL", DbContainerFixture.StartupError);
    }

    [TestMethod]
    [Timeout(60000, CooperativeCancellation = true)]
    public async Task NonAzureDomainSave_PublishesThroughAuditHandler_AndPersistsExactlyOneRow()
    {
        IntegrationTestSetup.AssertAvailable("Redis", RedisContainerFixture.StartupError);
        IntegrationTestSetup.AssertAvailable("SeaweedFS", SeaweedFsContainerFixture.StartupError);
        IntegrationTestSetup.AssertAvailable("RabbitMQ", RabbitMqBrokerFixture.StartupError);

        var ct = TestContext.CancellationToken;
        var connectionString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("auditpipeline");
        await MigrateAsync(connectionString, ct);

        var values = new Dictionary<string, string?>
        {
            [HostingLaneResolver.LaneConfigurationKey] = "NonAzure",
            ["ConnectionStrings:TaskFlowDbContextTrxn"] = connectionString,
            ["ConnectionStrings:TaskFlowDbContextQuery"] = connectionString,
            ["ConnectionStrings:TaskFlowFlowEngineDbContext"] = connectionString,
            ["ConnectionStrings:Redis1"] = RedisContainerFixture.ConnectionString,
            [$"{RabbitMqRegistration.OptionsSection}:ConnectionString"] = RabbitMqBrokerFixture.ConnectionString,
            ["Storage:S3:ServiceUrl"] = SeaweedFsContainerFixture.ServiceUrl,
            ["Storage:S3:PublicServiceUrl"] = SeaweedFsContainerFixture.ServiceUrl,
            ["Storage:S3:AccessKeyId"] = SeaweedFsContainerFixture.AccessKey,
            ["Storage:S3:SecretAccessKey"] = SeaweedFsContainerFixture.SecretKey,
            ["Storage:S3:ForcePathStyle"] = "true",
            ["AuditLogStorageSettings:Audit:SystemTenantId"] = SystemTenantId
        };
        foreach (var (key, value) in TestColumnEncryption.Configuration) values[key] = value;
        if (TestHostingLane.UsesMongoDb)
            values["ConnectionStrings:MongoDb1"] = MongoDbContainerFixture.ConnectionString;

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            EnvironmentName = Environments.Production
        });
        builder.Configuration.AddInMemoryCollection(values);
        builder.Services.RegisterInfrastructureServices(builder.Configuration);
        builder.Services.AddSingleton(TestCursorCodec.Instance);
        builder.Services.AddScoped<IMessageHandler<AuditEntry<string, Guid>>, AuditHandler>();
        builder.Services.AddScoped<IMessageHandler<AuditEntry<string, Guid?>>, AuditHandler>();

        using var host = builder.Build();
        host.AutoRegisterMessageHandlers();
        await host.StartAsync(ct);
        try
        {
            var tenantId = DomainId.From<TenantId>(Guid.CreateVersion7());
            var category = Category.Create(tenantId, $"Audited {Guid.NewGuid():N}").Value!;
            using (var scope = host.Services.CreateScope())
            {
                var factory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TaskFlowDbContextTrxn>>();
                await using var db = await factory.CreateDbContextAsync(ct);
                db.Categories.Add(category);
                await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);
            }

            var deadline = TimeProvider.System.GetUtcNow().AddSeconds(10);
            var auditCount = 0;
            do
            {
                await using var verify = DbContainerFixture.CreateQueryContext(connectionString);
                auditCount = await verify.AuditLog.CountAsync(ct);
                if (auditCount != 0) break;
                await Task.Delay(TimeSpan.FromMilliseconds(50), ct);
            } while (TimeProvider.System.GetUtcNow() < deadline);

            Assert.AreEqual(1, auditCount, "one domain SaveChanges must produce one relational audit row");
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            await using var finalVerify = DbContainerFixture.CreateQueryContext(connectionString);
            Assert.AreEqual(1, await finalVerify.AuditLog.CountAsync(ct),
                "the audit row must not recursively audit itself or be written through both bus and direct sinks");
        }
        finally
        {
            await host.StopAsync(ct);
        }
    }

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Append_PersistsEveryAuditField_AndRetentionRespectsTheCutoff()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("auditrel");
        await MigrateAsync(connString, ct);

        var tenantId = Guid.NewGuid();
        var entry = new AuditEntry<string, Guid>
        {
            Id = Guid.CreateVersion7(),
            AuditId = "integration-user",
            TenantId = tenantId,
            EntityType = "TaskItem",
            EntityKey = Guid.NewGuid().ToString(),
            Status = AuditStatus.Success,
            Action = "Create",
            StartTime = TimeSpan.FromMilliseconds(25),
            ElapsedTime = TimeSpan.FromMilliseconds(7),
            Metadata = "{\"source\":\"relational-test\"}"
        };

        await using (var db = DbContainerFixture.CreateTrxnContext(connString))
        {
            await new RelationalAuditLogRepository(db, SystemTenantId).AppendAsync(entry, ct);
        }

        await using (var verify = DbContainerFixture.CreateQueryContext(connString))
        {
            var persisted = await verify.AuditLog.AsNoTracking().SingleAsync(e => e.Id == entry.Id, ct);

            Assert.AreEqual(tenantId.ToString(), persisted.TenantId,
                "the key is tenant-first so one tenant's trail is a range scan");
            Assert.AreEqual(entry.AuditId, persisted.AuditId);
            Assert.AreEqual(entry.EntityType, persisted.EntityType);
            Assert.AreEqual(entry.EntityKey, persisted.EntityKey);
            Assert.AreEqual(entry.Action, persisted.Action);
            Assert.AreEqual(entry.Status.ToString(), persisted.Status);
            Assert.AreEqual(entry.StartTime.Ticks, persisted.StartTimeTicks);
            Assert.AreEqual(entry.ElapsedTime.Ticks, persisted.ElapsedTimeTicks);
            Assert.AreEqual(entry.Metadata, persisted.Metadata);
            Assert.IsNull(persisted.Error);
        }

        // An entry with no tenant is bucketed under the configured sentinel, not left null: the tenant is
        // part of the primary key.
        var systemEntry = new AuditEntry<string, Guid?>
        {
            Id = Guid.CreateVersion7(),
            AuditId = "system",
            TenantId = null,
            EntityType = "Retention",
            EntityKey = "sweep",
            Status = AuditStatus.Success,
            Action = "Purge"
        };

        await using (var db = DbContainerFixture.CreateTrxnContext(connString))
        {
            await new RelationalAuditLogRepository(db, SystemTenantId).AppendAsync(systemEntry, ct);
        }

        await using (var verify = DbContainerFixture.CreateQueryContext(connString))
        {
            var persisted = await verify.AuditLog.AsNoTracking().SingleAsync(e => e.Id == systemEntry.Id, ct);
            Assert.AreEqual(SystemTenantId, persisted.TenantId);
        }

        await using (var db = DbContainerFixture.CreateTrxnContext(connString))
        {
            var repository = new RelationalAuditLogRepository(db, SystemTenantId);

            // Recorded now: outside a cutoff in the past and kept, inside a cutoff in the future and removed.
            Assert.AreEqual(0, await repository.PurgeOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-1), ct));
            Assert.AreEqual(2, await repository.PurgeOlderThanAsync(DateTimeOffset.UtcNow.AddMinutes(5), ct));
        }

        await using (var verify = DbContainerFixture.CreateQueryContext(connString))
        {
            Assert.AreEqual(0, await verify.AuditLog.CountAsync(ct));
        }
    }

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task PurgeOlderThan_WalksTheWindowInBatches_AndKeepsRowsInsideIt()
    {
        var ct = TestContext.CancellationToken;
        var connString = await DbContainerFixture.CreateEmptyDatabaseConnectionStringAsync("auditpurge");
        await MigrateAsync(connString, ct);

        var cutoffUtc = DateTimeOffset.UtcNow.AddDays(-30);
        await SeedAsync(connString, RetentionRowCount, cutoffUtc.AddDays(-1), ct);
        // Rows inside the retention window that the sweep must not touch.
        await SeedAsync(connString, 5, cutoffUtc.AddDays(1), ct);

        await using (var db = DbContainerFixture.CreateTrxnContext(connString))
        {
            var repository = new RelationalAuditLogRepository(db, SystemTenantId, RetentionBatchSize);

            // 250 expired rows at 100 per batch: three batches, the last one short, which is also the loop's
            // stopping rule. A single unbounded DELETE would report the same total and prove nothing.
            Assert.AreEqual(RetentionRowCount, await repository.PurgeOlderThanAsync(cutoffUtc, ct));
        }

        await using (var verify = DbContainerFixture.CreateQueryContext(connString))
        {
            Assert.AreEqual(5, await verify.AuditLog.CountAsync(ct), "rows inside the window survive");
            Assert.AreEqual(0, await verify.AuditLog.CountAsync(e => e.RecordedUtc < cutoffUtc, ct));
        }
    }

    public TestContext TestContext { get; set; } = null!;

    private static async Task MigrateAsync(string connString, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        await db.Database.MigrateAsync(ct);
    }

    /// <summary>
    /// Seeds rows at a chosen <c>RecordedUtc</c>. The repository always stamps "now", so the retention
    /// window can only be set up by writing the rows directly.
    /// </summary>
    private static async Task SeedAsync(string connString, int count, DateTimeOffset recordedUtc, CancellationToken ct)
    {
        await using var db = DbContainerFixture.CreateTrxnContext(connString);
        for (var i = 0; i < count; i++)
        {
            db.AuditLog.Add(new AuditLogRecord
            {
                TenantId = SystemTenantId,
                // Distinct timestamps, so the composite primary key cannot collide.
                RecordedUtc = recordedUtc.AddMilliseconds(i),
                Id = Guid.CreateVersion7(),
                AuditId = "seed",
                EntityType = "TaskItem",
                EntityKey = i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Action = "Update",
                Status = AuditStatus.Success.ToString()
            });
        }

        await db.SaveChangesAsync(OptimisticConcurrencyWinner.ClientWins, cancellationToken: ct);
    }
}
