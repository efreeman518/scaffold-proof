using MongoDB.Bson;
using MongoDB.Driver;
using TaskFlow.Application.Contracts.Storage;
using TaskFlow.Hosting;
using TaskFlow.Infrastructure.Repositories.MongoDb;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>MongoDB provider contract against a standalone MongoDB container.</summary>
[TestClass]
[TestCategory("Integration")]
public sealed class MongoTaskViewRepositoryTests
{
    private const int ConcurrentPatchers = 8;

    public TestContext TestContext { get; set; } = null!;

    [TestInitialize]
    public void TestSetup()
    {
        IntegrationTestSetup.RequireLane(HostingLane.NonAzure, requireMongoDb: true);
        IntegrationTestSetup.AssertAvailable("MongoDB", MongoDbContainerFixture.StartupError);
    }

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task Repository_PreservesTenantUpsertPatchDeleteAndIndexContracts()
    {
        var ct = TestContext.CancellationToken;
        var settings = NewSettings();
        var repository = new MongoTaskViewRepository(MongoDbContainerFixture.ConnectionString, settings);
        var client = new MongoClient(MongoDbContainerFixture.ConnectionString);
        var database = client.GetDatabase(settings.DatabaseName);

        try
        {
            await repository.EnsureIndexesAsync(ct);
            await repository.CheckConnectivityAsync(ct);

            const string sharedId = "shared-id";
            var tenantA = $"tenant-a-{Guid.NewGuid():N}";
            var tenantB = $"tenant-b-{Guid.NewGuid():N}";
            var timestamp = DateTimeOffset.UtcNow.AddMilliseconds(-DateTimeOffset.UtcNow.Millisecond);
            var first = NewDto(tenantA, sharedId, "first", timestamp);
            first.Description = "document body";
            first.Tags = ["alpha", "beta"];
            first.CommentCount = 2;

            await repository.UpsertAsync(first, ct);
            await repository.UpsertAsync(NewDto(tenantB, sharedId, "other tenant", timestamp), ct);

            var loaded = await repository.GetAsync(sharedId, tenantA, ct);
            Assert.IsNotNull(loaded);
            Assert.AreEqual("first", loaded.Title);
            Assert.AreEqual("document body", loaded.Description);
            CollectionAssert.AreEqual(new[] { "alpha", "beta" }, loaded.Tags);
            Assert.IsNull(await repository.GetAsync(sharedId, "foreign-tenant", ct));

            var replacement = NewDto(tenantA, sharedId, "replacement", timestamp.AddMinutes(1));
            await repository.UpsertAsync(replacement, ct);
            Assert.AreEqual("replacement", (await repository.GetAsync(sharedId, tenantA, ct))?.Title);

            await Task.WhenAll(Enumerable.Range(0, ConcurrentPatchers).Select(i => repository.PatchCountersAsync(
                sharedId,
                tenantA,
                new Dictionary<string, int>
                {
                    ["commentCount"] = 1,
                    ["checklistTotal"] = 2,
                    ["checklistCompleted"] = -1,
                    ["attachmentCount"] = 0
                },
                timestamp.AddMinutes(2).AddSeconds(i),
                ct)));

            loaded = await repository.GetAsync(sharedId, tenantA, ct);
            Assert.IsNotNull(loaded);
            Assert.AreEqual(ConcurrentPatchers, loaded.CommentCount);
            Assert.AreEqual(ConcurrentPatchers * 2, loaded.ChecklistTotal);
            Assert.AreEqual(-ConcurrentPatchers, loaded.ChecklistCompleted);
            Assert.AreEqual(0, loaded.AttachmentCount);

            await repository.PatchCountersAsync("missing", tenantA,
                new Dictionary<string, int> { ["commentCount"] = 1 }, timestamp, ct);

            var indexes = await database.GetCollection<BsonDocument>(settings.CollectionName)
                .Indexes.ListAsync(cancellationToken: ct);
            var indexDocuments = await indexes.ToListAsync(ct);
            Assert.IsTrue(indexDocuments.Any(e => e["name"] == MongoTaskViewRepository.IdentityIndexName
                && e.GetValue("unique", false).ToBoolean()));
            Assert.IsTrue(indexDocuments.Any(e => e["name"] == MongoTaskViewRepository.PagingIndexName));

            await repository.DeleteAsync(sharedId, tenantA, ct);
            await repository.DeleteAsync(sharedId, tenantA, ct);
            Assert.IsNull(await repository.GetAsync(sharedId, tenantA, ct));
            Assert.IsNotNull(await repository.GetAsync(sharedId, tenantB, ct));
        }
        finally
        {
            await client.DropDatabaseAsync(settings.DatabaseName, ct);
        }
    }

    [TestMethod]
    [Timeout(300000, CooperativeCancellation = true)]
    public async Task KeysetPaging_WalksEveryTenantRowOnce_WithTimestampTies()
    {
        const int rowCount = 25;
        const int pageSize = 10;
        var ct = TestContext.CancellationToken;
        var settings = NewSettings();
        var repository = new MongoTaskViewRepository(MongoDbContainerFixture.ConnectionString, settings);
        var client = new MongoClient(MongoDbContainerFixture.ConnectionString);

        try
        {
            await repository.EnsureIndexesAsync(ct);
            var tenantId = $"tenant-{Guid.NewGuid():N}";
            var otherTenant = $"tenant-{Guid.NewGuid():N}";
            var start = new DateTimeOffset(2026, 9, 12, 12, 0, 0, TimeSpan.Zero);

            for (var i = 0; i < rowCount; i++)
                await repository.UpsertAsync(NewDto(tenantId, $"row-{i:D2}", $"row {i}", start.AddMinutes(i / 3)), ct);
            await repository.UpsertAsync(NewDto(otherTenant, "foreign", "foreign", start), ct);

            var seen = new List<string>();
            string? token = null;
            do
            {
                var page = await repository.QueryByTenantAsync(tenantId, pageSize, token, ct);
                seen.AddRange(page.Items.Select(e => e.Id));
                token = page.ContinuationToken;
            }
            while (token is not null);

            Assert.AreEqual(rowCount, seen.Count);
            Assert.AreEqual(rowCount, seen.Distinct(StringComparer.Ordinal).Count());
            CollectionAssert.DoesNotContain(seen, "foreign");

            var foreignToken = TaskFlow.Infrastructure.Repositories.TaskViewKeysetToken.Encode(otherTenant, start, "foreign");
            await Assert.ThrowsExactlyAsync<ArgumentException>(
                () => repository.QueryByTenantAsync(tenantId, pageSize, foreignToken, ct));
        }
        finally
        {
            await client.DropDatabaseAsync(settings.DatabaseName, ct);
        }
    }

    private static MongoTaskViewSettings NewSettings() =>
        new($"taskflow-{Guid.NewGuid():N}", "task-views");

    private static TaskViewDto NewDto(
        string tenantId, string id, string title, DateTimeOffset lastModifiedUtc) => new()
    {
        Id = id,
        TenantId = tenantId,
        Title = title,
        Status = "InProgress",
        Priority = "High",
        CreatedUtc = lastModifiedUtc.AddDays(-1),
        LastModifiedUtc = lastModifiedUtc
    };
}
