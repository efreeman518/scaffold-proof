using Azure.Data.Tables;
using EF.Audit.Contracts;
using EF.Common.Contracts;
using Microsoft.Extensions.Azure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TaskFlow.Application.Contracts.Concurrency;
using TaskFlow.Infrastructure.Storage;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// Validates <c>AuditLogRepository</c> against real Azurite Table Storage: the tenant-day partition key,
/// the row key shape (<c>..._{Id:N}</c>), the round trip of audit metadata, and the retention sweep.
/// The table is created by the test, mirroring the EnsureExternalResources startup task: the repository
/// deliberately no longer provisions it on every append.
/// Component tier: exercises only Azurite via a standalone <c>AzuriteContainerFixture</c> (started by
/// <c>IntegrationTestSetup</c>) - no API, no Function, no Aspire graph.
/// </summary>
[TestClass]
[TestCategory("Integration")]
[DoNotParallelize]
public class AuditLogRepositoryAzuriteTests
{
    /// <summary>Classifies Docker unavailability separately from an Azurite startup failure.</summary>
    [TestInitialize]
    public void TestSetup()
    {
        IntegrationTestSetup.AssertAvailable("Azurite", AzuriteContainerFixture.StartupError);
    }

    /// <summary>Verifies that given audit entry, when append to azurite, then table entity persisted with expected keys.</summary>
    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Given_AuditEntry_When_AppendAsyncToAzurite_Then_TableEntityPersistedWithExpectedKeys()
    {
        var ct = CancellationToken.None;

        var connectionString = AzuriteContainerFixture.ConnectionString;
        Assert.IsFalse(string.IsNullOrWhiteSpace(connectionString));

        var tableName = $"audit{Guid.NewGuid():N}"[..31];
        var tableServiceClient = new TableServiceClient(connectionString);
        var repository = new AuditLogRepository(
            new TestTableServiceClientFactory(tableServiceClient),
            Options.Create(new AuditLogStorageSettings
            {
                TableName = tableName,
                Audit = new AuditSettings { SystemTenantId = "_system" }
            }),
            NullLogger<AuditLogRepository>.Instance);

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
            Metadata = "{\"source\":\"azurite-test\"}"
        };

        var tableClient = tableServiceClient.GetTableClient(tableName);
        // Provisioned once, the way the startup task does it.
        await tableClient.CreateIfNotExistsAsync(ct);

        try
        {
            await repository.AppendAsync(entry, ct);

            // Keys derive from the message's own UUIDv7 id, not the writing clock, so the partition a
            // replay lands in is reproducible from the entry alone (A1 replay idempotency).
            var partitionKey = AuditLogRepository.PartitionKey(
                tenantId.ToString(), UuidV7.TimestampOf(entry.Id));
            var persisted = await ReadSingleEntityAsync(tableClient, partitionKey);

            Assert.IsNotNull(persisted);
            StringAssert.StartsWith(persisted.PartitionKey, $"{tenantId}|",
                "the partition key carries the tenant and the day so retention can drop whole days");
            Assert.IsTrue(persisted.RowKey.EndsWith($"_{entry.Id:N}", StringComparison.Ordinal));
            Assert.AreEqual(
                AuditLogRepository.RowKey(UuidV7.TimestampOf(entry.Id), entry.Id), persisted.RowKey,
                "the row key must be reproducible from the message alone so a redelivery overwrites its own row");
            Assert.AreEqual(entry.AuditId, persisted.AuditId);
            Assert.AreEqual(tenantId.ToString(), persisted.TenantId);
            Assert.AreEqual(entry.EntityType, persisted.EntityType);
            Assert.AreEqual(entry.EntityKey, persisted.EntityKey);
            Assert.AreEqual(entry.Action, persisted.Action);
            Assert.AreEqual(entry.Status.ToString(), persisted.Status);
            Assert.AreEqual(entry.Metadata, persisted.Metadata);

            // Retention: an entry recorded now is outside a cutoff in the past and survives, and inside a
            // cutoff in the future and is removed.
            Assert.AreEqual(0, await repository.PurgeOlderThanAsync(DateTimeOffset.UtcNow.AddDays(-1), ct));
            Assert.AreEqual(1, await repository.PurgeOlderThanAsync(DateTimeOffset.UtcNow.AddMinutes(5), ct));
            Assert.IsFalse(await AnyEntityAsync(tableClient, partitionKey));
        }
        finally
        {
            await tableServiceClient.DeleteTableAsync(tableName, TestContext.CancellationToken);
        }
    }

    /// <summary>Verifies read single entity behavior and protects the expected test contract.</summary>
    private static async Task<AuditLogTableEntity> ReadSingleEntityAsync(TableClient tableClient, string partitionKey)
    {
        await foreach (var entity in tableClient.QueryAsync<AuditLogTableEntity>(
            entity => entity.PartitionKey == partitionKey))
        {
            return entity;
        }

        Assert.Fail("Expected an audit entity to be written to Azurite.");
        throw new InvalidOperationException("Unreachable");
    }

    /// <summary>True when the partition still holds any entity.</summary>
    private static async Task<bool> AnyEntityAsync(TableClient tableClient, string partitionKey)
    {
        await foreach (var _ in tableClient.QueryAsync<AuditLogTableEntity>(e => e.PartitionKey == partitionKey))
            return true;

        return false;
    }

    /// <summary>Builds test table service client test hosts with deterministic dependencies for repeatable test execution.</summary>
    private sealed class TestTableServiceClientFactory(TableServiceClient client) : IAzureClientFactory<TableServiceClient>
    {
        /// <summary>Creates client used by the surrounding test cases.</summary>
        public TableServiceClient CreateClient(string name) => client;
    }

    public TestContext TestContext { get; set; } = null!;
}
