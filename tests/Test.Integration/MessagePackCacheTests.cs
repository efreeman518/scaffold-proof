using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Reads;
using TaskFlow.Domain.Shared.Enums;
using TaskFlow.Infrastructure.Caching;
using Test.Integration.Infrastructure;

namespace Test.Integration;

/// <summary>
/// D-048: proves the binary L2 arm against a real Redis. The unit tier already round-trips the cached
/// DTOs through both serializers in process; what it cannot show is that a value one replica wrote to
/// Redis is a value another replica can read - which is the only thing an L2 cache is for, and the exact
/// place a serializer choice fails. A contractless MessagePack payload that serializes cleanly and
/// deserializes to a default-valued object would pass every in-process test and quietly turn the L2 tier
/// into a slower miss.
///
/// Both cases below are two independent service providers standing in for two replicas over one Redis,
/// matching the pattern in RedisCacheAndLimiterTests.
/// Component tier: a real Redis Testcontainer.
/// </summary>
[TestClass]
[TestCategory("Integration")]
public class MessagePackCacheTests
{
    private static readonly DateTimeOffset GeneratedAt = new(2026, 9, 8, 14, 30, 15, TimeSpan.Zero);

    /// <summary>Marks the test Inconclusive when the Redis container failed to start.</summary>
    [TestInitialize]
    public void TestSetup() => IntegrationTestSetup.AssertAvailable("Redis", RedisContainerFixture.StartupError);

    /// <summary>A metadata snapshot written by one replica is read intact from the other's L2.</summary>
    [TestMethod]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task Given_MessagePackSerializer_When_MetadataCrossesReplicas_Then_EveryFieldSurvives()
    {
        var tenantId = Guid.NewGuid();
        var key = new CacheKey(CacheKind.TaskMetadata, tenantId);
        var snapshot = new TaskMetadataDto
        {
            Categories =
            [
                new CategoryDto
                {
                    Id = Guid.NewGuid(), Version = 3, TenantId = tenantId, Name = "Ops",
                    Description = "Operational work", SortOrder = 2, IsActive = true,
                    ParentCategoryId = Guid.NewGuid()
                },
                new CategoryDto { Id = Guid.NewGuid(), TenantId = tenantId, Name = "Ad hoc" }
            ],
            Tags =
            [
                new TagDto { Id = Guid.NewGuid(), Version = 1, TenantId = tenantId, Name = "urgent", Color = "#ff0000" },
                new TagDto { Id = Guid.NewGuid(), TenantId = tenantId, Name = "later" }
            ],
            GeneratedAtUtc = GeneratedAt
        };

        var writer = BuildCache(CacheSerializer.MessagePack);
        var reader = BuildCache(CacheSerializer.MessagePack);

        var factoryCalls = 0;
        Task<TaskMetadataDto> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref factoryCalls);
            return Task.FromResult(snapshot);
        }

        await writer.GetOrSetAsync(key, Factory, CacheProfile.Metadata, TestContext.CancellationToken);
        var actual = await reader.GetOrSetAsync(key, Factory, CacheProfile.Metadata, TestContext.CancellationToken);

        Assert.AreEqual(1, factoryCalls,
            "the second replica read the MessagePack payload out of L2 instead of rebuilding it");
        Assert.AreEqual(snapshot.GeneratedAtUtc, actual.GeneratedAtUtc);
        CollectionAssert.AreEqual(snapshot.Categories.ToArray(), actual.Categories.ToArray());
        CollectionAssert.AreEqual(snapshot.Tags.ToArray(), actual.Tags.ToArray());
    }

    /// <summary>
    /// The summary snapshot crosses replicas too. It gets its own case because its status buckets are a
    /// positional record inside a read-only list - the shape that broke the JSON arm's L2 reads before
    /// ReferenceHandler.Preserve was removed from the cache serializer options.
    /// </summary>
    [TestMethod]
    [DataRow(CacheSerializer.Json)]
    [DataRow(CacheSerializer.MessagePack)]
    [Timeout(180000, CooperativeCancellation = true)]
    public async Task Given_EitherSerializer_When_SummaryCrossesReplicas_Then_EveryFieldSurvives(
        CacheSerializer format)
    {
        var tenantId = Guid.NewGuid();
        var key = new CacheKey(CacheKind.TaskSummary, tenantId);
        var snapshot = new TaskItemSummaryDto
        {
            ByStatus =
            [
                new TaskItemStatusCountDto(TaskItemStatus.Open, 7),
                new TaskItemStatusCountDto(TaskItemStatus.Blocked, 2),
                new TaskItemStatusCountDto(TaskItemStatus.Completed, 3)
            ],
            Overdue = 4,
            Total = 12,
            GeneratedAtUtc = GeneratedAt
        };

        var writer = BuildCache(format);
        var reader = BuildCache(format);

        var factoryCalls = 0;
        Task<TaskItemSummaryDto> Factory(CancellationToken _)
        {
            Interlocked.Increment(ref factoryCalls);
            return Task.FromResult(snapshot);
        }

        await writer.GetOrSetAsync(key, Factory, CacheProfile.Summary, TestContext.CancellationToken);
        var actual = await reader.GetOrSetAsync(key, Factory, CacheProfile.Summary, TestContext.CancellationToken);

        Assert.AreEqual(1, factoryCalls, "the second replica read the payload out of L2 instead of rebuilding it");
        Assert.AreEqual(snapshot.Total, actual.Total);
        Assert.AreEqual(snapshot.Overdue, actual.Overdue);
        Assert.AreEqual(snapshot.GeneratedAtUtc, actual.GeneratedAtUtc);
        CollectionAssert.AreEqual(snapshot.ByStatus.ToArray(), actual.ByStatus.ToArray());
    }

    /// <summary>Builds one cache "replica" over the shared Redis with the given L2 serializer.</summary>
    private static ITaskFlowCache BuildCache(CacheSerializer format)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CacheSettings:0:Name"] = AppConstants.DEFAULT_CACHE,
                ["CacheSettings:0:Serializer"] = format.ToString(),
                ["CacheSettings:0:RedisConnectionStringName"] = "Redis1",
                ["ConnectionStrings:Redis1"] = RedisContainerFixture.ConnectionString
            })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IHostEnvironment>(new MessagePackTestHostEnvironment());
        services.AddTaskFlowCaching(config);

        return services.BuildServiceProvider().GetRequiredService<ITaskFlowCache>();
    }

    /// <summary>Minimal host environment: the cache only reads the environment name for its key prefix.</summary>
    private sealed class MessagePackTestHostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "IntegrationTest";
        public string ApplicationName { get; set; } = "Test.Integration";
        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }

    /// <summary>MSTest-injected context; supplies the per-test cancellation token.</summary>
    public TestContext TestContext { get; set; } = null!;
}
