using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Moq;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Infrastructure.Caching;
using TaskFlow.Observability.Meters;
using ZiggyCreatures.Caching.Fusion;

namespace Test.Unit.Infrastructure;

/// <summary>
/// Pins the cache key and tag shapes. Both are contracts with things outside this process: the key separates
/// deployments sharing one Redis and retires entries when a snapshot's shape changes, and the tags are what
/// a writer names when it invalidates without knowing which snapshots exist. A silent change to either is a
/// cross-environment leak or a stale read that no other test would catch.
/// Pure-unit tier: a real FusionCache instance with no distributed tier.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class TaskFlowCacheKeyTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>The key carries environment, schema version, tenant, and kind, in that order.</summary>
    [TestMethod]
    public void Render_ProducesTheDocumentedKeyShape()
    {
        var cache = Build(new CacheSettings { Name = AppConstants.DEFAULT_CACHE, SchemaVersion = 1 }, "Production");

        Assert.AreEqual(
            $"Production:1:{TenantId:N}:{CacheKind.TaskSummary}",
            cache.Render(new CacheKey(CacheKind.TaskSummary, TenantId)));
    }

    /// <summary>A discriminator is appended, so variants of one kind never collide.</summary>
    [TestMethod]
    public void Render_AppendsTheDiscriminator()
    {
        var cache = Build(new CacheSettings { Name = AppConstants.DEFAULT_CACHE, SchemaVersion = 1 }, "Development");

        Assert.AreEqual(
            $"Development:1:{TenantId:N}:{CacheKind.TaskMetadata}:active",
            cache.Render(new CacheKey(CacheKind.TaskMetadata, TenantId, "active")));
    }

    /// <summary>Bumping the schema version changes every key, which is how a shape change retires old entries.</summary>
    [TestMethod]
    public void Render_SchemaVersionBump_ChangesEveryKey()
    {
        var key = new CacheKey(CacheKind.TaskSummary, TenantId);
        var v1 = Build(new CacheSettings { Name = AppConstants.DEFAULT_CACHE, SchemaVersion = 1 }, "Production");
        var v2 = Build(new CacheSettings { Name = AppConstants.DEFAULT_CACHE, SchemaVersion = 2 }, "Production");

        Assert.AreNotEqual(v1.Render(key), v2.Render(key));
    }

    /// <summary>Two environments sharing one Redis never read each other's entries.</summary>
    [TestMethod]
    public void Render_EnvironmentsAreIsolated()
    {
        var key = new CacheKey(CacheKind.TaskSummary, TenantId);
        var staging = Build(new CacheSettings { Name = AppConstants.DEFAULT_CACHE }, "Staging");
        var production = Build(new CacheSettings { Name = AppConstants.DEFAULT_CACHE }, "Production");

        Assert.AreNotEqual(staging.Render(key), production.Render(key));
    }

    /// <summary>The summary depends on tasks; the metadata snapshot depends on categories and tags.</summary>
    [TestMethod]
    public void TagsFor_DeclareEveryEntityTheSnapshotIsBuiltFrom()
    {
        CollectionAssert.AreEquivalent(
            new[] { CacheTags.Tenant(TenantId), CacheTags.Entity(TenantId, CacheTags.TaskItem) },
            FusionTaskFlowCache.TagsFor(new CacheKey(CacheKind.TaskSummary, TenantId)).ToArray());

        CollectionAssert.AreEquivalent(
            new[]
            {
                CacheTags.Tenant(TenantId),
                CacheTags.Entity(TenantId, CacheTags.Category),
                CacheTags.Entity(TenantId, CacheTags.Tag)
            },
            FusionTaskFlowCache.TagsFor(new CacheKey(CacheKind.TaskMetadata, TenantId)).ToArray());
    }

    /// <summary>Tags are tenant-scoped, so one tenant's write never evicts another tenant's snapshot.</summary>
    [TestMethod]
    public void Tags_AreTenantScoped()
    {
        var other = Guid.NewGuid();

        Assert.AreNotEqual(CacheTags.Tenant(TenantId), CacheTags.Tenant(other));
        Assert.AreNotEqual(
            CacheTags.Entity(TenantId, CacheTags.TaskItem),
            CacheTags.Entity(other, CacheTags.TaskItem));
        Assert.StartsWith(CacheTags.Tenant(TenantId), CacheTags.Entity(TenantId, CacheTags.TaskItem));
    }

    /// <summary>Builds the cache over a real named FusionCache instance with no distributed tier.</summary>
    private static FusionTaskFlowCache Build(CacheSettings settings, string environmentName)
    {
        var services = new ServiceCollection();
        services.AddFusionCache(settings.Name);
        var provider = services.BuildServiceProvider();

        var environment = new Mock<IHostEnvironment>();
        environment.SetupGet(e => e.EnvironmentName).Returns(environmentName);

        return new FusionTaskFlowCache(
            provider.GetRequiredService<IFusionCacheProvider>(),
            settings,
            new CacheMeter(),
            environment.Object);
    }
}
