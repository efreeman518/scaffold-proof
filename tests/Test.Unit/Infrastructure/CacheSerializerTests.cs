using EF.Cache;
using TaskFlow.Application.Models;
using TaskFlow.Application.Models.Reads;
using TaskFlow.Domain.Shared.Enums;

namespace Test.Unit.Infrastructure;

/// <summary>
/// D-048: pins both arms of EF.Cache's <c>CacheSettings:Serializer</c> against the shapes actually cached - the
/// metadata and summary snapshots. The MessagePack arm is contractless, so nothing on the DTOs declares
/// how they serialize; the only thing standing between a config flip and an L2 that silently refactories
/// every entry is that these types round-trip through both formats.
/// Pure-unit tier: the serializers alone, no cache instance and no Redis.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class CacheSerializerTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset GeneratedAt = new(2026, 9, 8, 14, 30, 15, TimeSpan.Zero);

    /// <summary>The default is JSON: a readable L2 is worth more than the bytes until someone opts out.</summary>
    [TestMethod]
    public void Given_DefaultSettings_When_SerializerCreated_Then_ItIsJson()
    {
        Assert.AreEqual(CacheSerializer.Json, new CacheSettings().Serializer);
        Assert.Contains("SystemTextJson", CacheServiceCollectionExtensions.CreateSerializer(new CacheSettings()).GetType().FullName!);
    }

    /// <summary>The MessagePack arm resolves to the Neuecc serializer, not silently back to JSON.</summary>
    [TestMethod]
    public void Given_MessagePackSettings_When_SerializerCreated_Then_ItIsMessagePack()
    {
        var serializer = CacheServiceCollectionExtensions.CreateSerializer(
            new CacheSettings { Serializer = CacheSerializer.MessagePack });

        Assert.Contains("NeueccMessagePack", serializer.GetType().FullName!);
    }

    /// <summary>Both formats round-trip the metadata snapshot with every field intact.</summary>
    [TestMethod]
    [DataRow(CacheSerializer.Json)]
    [DataRow(CacheSerializer.MessagePack)]
    public void Given_MetadataSnapshot_When_RoundTripped_Then_EveryFieldSurvives(CacheSerializer format)
    {
        var snapshot = new TaskMetadataDto
        {
            Categories =
            [
                new CategoryDto
                {
                    Id = Guid.NewGuid(), Version = 3, TenantId = TenantId, Name = "Ops",
                    Description = "Operational work", SortOrder = 2, IsActive = true,
                    ParentCategoryId = Guid.NewGuid()
                },
                new CategoryDto { Id = Guid.NewGuid(), TenantId = TenantId, Name = "Ad hoc" }
            ],
            Tags =
            [
                new TagDto { Id = Guid.NewGuid(), Version = 1, TenantId = TenantId, Name = "urgent", Color = "#ff0000" },
                new TagDto { Id = Guid.NewGuid(), TenantId = TenantId, Name = "later" }
            ],
            GeneratedAtUtc = GeneratedAt
        };

        var actual = RoundTrip(snapshot, format);

        Assert.AreEqual(snapshot.GeneratedAtUtc, actual.GeneratedAtUtc);
        CollectionAssert.AreEqual(snapshot.Categories.ToArray(), actual.Categories.ToArray());
        CollectionAssert.AreEqual(snapshot.Tags.ToArray(), actual.Tags.ToArray());
    }

    /// <summary>Both formats round-trip the summary snapshot, enum status buckets included.</summary>
    [TestMethod]
    [DataRow(CacheSerializer.Json)]
    [DataRow(CacheSerializer.MessagePack)]
    public void Given_SummarySnapshot_When_RoundTripped_Then_EveryFieldSurvives(CacheSerializer format)
    {
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

        var actual = RoundTrip(snapshot, format);

        Assert.AreEqual(snapshot.Overdue, actual.Overdue);
        Assert.AreEqual(snapshot.Total, actual.Total);
        Assert.AreEqual(snapshot.GeneratedAtUtc, actual.GeneratedAtUtc);
        CollectionAssert.AreEqual(snapshot.ByStatus.ToArray(), actual.ByStatus.ToArray());
    }

    /// <summary>
    /// The reason the binary arm exists: LZ4-compressed MessagePack is materially smaller on the wire
    /// than the JSON the same snapshot produces. Asserted as "smaller", not as a ratio, so the test
    /// pins the property that matters without becoming a benchmark that fails on a library update.
    /// </summary>
    [TestMethod]
    public void Given_MetadataSnapshot_When_SerializedBothWays_Then_MessagePackIsSmaller()
    {
        var snapshot = new TaskMetadataDto
        {
            Categories = [.. Enumerable.Range(0, 50).Select(i => new CategoryDto
            {
                Id = Guid.NewGuid(), Version = i, TenantId = TenantId,
                Name = $"Category {i}", Description = "A repeated description", SortOrder = i, IsActive = true
            })],
            Tags = [.. Enumerable.Range(0, 50).Select(i => new TagDto
            {
                Id = Guid.NewGuid(), Version = i, TenantId = TenantId, Name = $"tag-{i}", Color = "#00ff00"
            })],
            GeneratedAtUtc = GeneratedAt
        };

        var json = Serialize(snapshot, CacheSerializer.Json).Length;
        var messagePack = Serialize(snapshot, CacheSerializer.MessagePack).Length;

        Assert.IsLessThan(json, messagePack,
            $"MessagePack produced {messagePack} bytes against JSON's {json}");
    }

    private static byte[] Serialize<T>(T value, CacheSerializer format) =>
        CacheServiceCollectionExtensions.CreateSerializer(new CacheSettings { Serializer = format }).Serialize(value);

    private static T RoundTrip<T>(T value, CacheSerializer format)
    {
        var serializer = CacheServiceCollectionExtensions.CreateSerializer(new CacheSettings { Serializer = format });
        var actual = serializer.Deserialize<T>(serializer.Serialize(value));
        Assert.IsNotNull(actual);
        return actual!;
    }
}
