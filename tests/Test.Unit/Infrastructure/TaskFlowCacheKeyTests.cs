using EF.Cache;
using TaskFlow.Application.Contracts;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Infrastructure.Caching;

namespace Test.Unit.Infrastructure;

/// <summary>
/// Pins the cache key and tag shapes. Both are contracts with things outside this process: the key separates
/// deployments sharing one Redis and retires entries when a snapshot's shape changes, and the tags are what
/// a writer names when it invalidates without knowing which snapshots exist. A silent change to either is a
/// cross-environment leak or a stale read that no other test would catch.
/// Pure-unit tier: the package's static key renderer, no cache instance.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class TaskFlowCacheKeyTests
{
    private static readonly Guid TenantId = Guid.Parse("11111111-2222-3333-4444-555555555555");

    /// <summary>The key carries environment, schema version, kind, and tenant, in that order.</summary>
    [TestMethod]
    public void Render_ProducesTheDocumentedKeyShape()
    {
        Assert.AreEqual(
            $"Production:1:{CacheKind.TaskSummary}:{TenantId:N}",
            Render("Production", 1, TaskFlowCache.Key(CacheKind.TaskSummary, TenantId)));
    }

    /// <summary>A discriminator is appended, so variants of one kind never collide.</summary>
    [TestMethod]
    public void Render_AppendsTheDiscriminator()
    {
        Assert.AreEqual(
            $"Development:1:{CacheKind.TaskMetadata}:{TenantId:N}:active",
            Render("Development", 1, TaskFlowCache.Key(CacheKind.TaskMetadata, TenantId, "active")));
    }

    /// <summary>Bumping the schema version changes every key, which is how a shape change retires old entries.</summary>
    [TestMethod]
    public void Render_SchemaVersionBump_ChangesEveryKey()
    {
        var key = TaskFlowCache.Key(CacheKind.TaskSummary, TenantId);

        Assert.AreNotEqual(Render("Production", 1, key), Render("Production", 2, key));
    }

    /// <summary>Two environments sharing one Redis never read each other's entries.</summary>
    [TestMethod]
    public void Render_EnvironmentsAreIsolated()
    {
        var key = TaskFlowCache.Key(CacheKind.TaskSummary, TenantId);

        Assert.AreNotEqual(Render("Staging", 2, key), Render("Production", 2, key));
    }

    /// <summary>
    /// The registration stamps the deployment environment and the current schema version onto the settings the
    /// cache renders keys from. Both are code decisions the CacheSettings section cannot express.
    /// </summary>
    [TestMethod]
    public void ApplyTaskFlowDefaults_StampsEnvironmentSchemaVersionAndProfiles()
    {
        var settings = RegisterCachingServices.ApplyTaskFlowDefaults(
            new CacheSettings { Name = AppConstants.DEFAULT_CACHE }, "Production");

        Assert.AreEqual("Production", settings.KeyNamespace);
        Assert.AreEqual(RegisterCachingServices.SchemaVersion, settings.SchemaVersion);

        // The summary is held for seconds: a dashboard count is visibly wrong when stale, and the instance
        // default of 30 minutes would serve one for the whole poll interval of every client.
        Assert.AreEqual(5, settings.Profiles[CacheProfiles.Summary].DurationSeconds);
        Assert.AreEqual(500, settings.Profiles[CacheProfiles.Summary].FactorySoftTimeoutMilliseconds);
        Assert.AreEqual(300, settings.Profiles[CacheProfiles.Metadata].DurationSeconds);
        Assert.AreEqual(0.8f, settings.Profiles[CacheProfiles.Metadata].EagerRefreshThreshold);
    }

    /// <summary>A profile already configured for a deployment wins over the code default.</summary>
    [TestMethod]
    public void ApplyTaskFlowDefaults_KeepsAConfiguredProfile()
    {
        var settings = new CacheSettings();
        settings.Profiles[CacheProfiles.Summary] = new CacheProfileOptions { DurationSeconds = 30 };

        RegisterCachingServices.ApplyTaskFlowDefaults(settings, "Production");

        Assert.AreEqual(30, settings.Profiles[CacheProfiles.Summary].DurationSeconds);
    }

    /// <summary>The summary depends on tasks; the metadata snapshot depends on categories and tags.</summary>
    [TestMethod]
    public void TagsFor_DeclareEveryEntityTheSnapshotIsBuiltFrom()
    {
        CollectionAssert.AreEquivalent(
            new[] { CacheTags.Tenant(TenantId), CacheTags.Entity(TenantId, CacheTags.TaskItem) },
            TaskFlowCache.TagsFor(CacheKind.TaskSummary, TenantId).ToArray());

        CollectionAssert.AreEquivalent(
            new[]
            {
                CacheTags.Tenant(TenantId),
                CacheTags.Entity(TenantId, CacheTags.Category),
                CacheTags.Entity(TenantId, CacheTags.Tag)
            },
            TaskFlowCache.TagsFor(CacheKind.TaskMetadata, TenantId).ToArray());
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

    private static string Render(string environmentName, int schemaVersion, CacheKey key) =>
        TypedCache.RenderKey(environmentName, schemaVersion, key);
}
