using System.Reflection;
using TaskFlow.Application.Contracts.Caching;
using TaskFlow.Application.Models.Reads;

namespace Test.Architecture;

/// <summary>
/// Rules that keep the cache behind <c>EF.Cache.ITypedCache</c>. Application code states what it wants cached
/// and what a write invalidates; expiry policy, the distributed tier, the backplane, and fail-safe live in the
/// package. A service reaching for FusionCache directly would set its own durations and its own keys, which is
/// how a cache starts serving two different answers for the same question.
/// Pure-unit tier: reflection over the built assemblies, no host.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class CachingArchitectureTests : BaseTest
{
    private static readonly string[] ForbiddenCacheTypes =
        ["IFusionCache", "FusionCache", "IFusionCacheProvider", "IDistributedCache", "IMemoryCache"];

    /// <summary>
    /// Every snapshot type handed to <c>ITypedCache.GetOrSetAsync</c>, one per <see cref="CacheKind"/>. The
    /// list is declared rather than discovered because a call site's generic argument is not reachable by
    /// reflection; the count assertion below is what keeps it honest when a kind is added.
    /// </summary>
    private static readonly Type[] CachedSnapshotTypes = [typeof(TaskItemSummaryDto), typeof(TaskMetadataDto)];

    /// <summary>
    /// Every cached snapshot type is publicly visible. D-048's MessagePack arm uses the contractless resolver,
    /// which emits its formatter at run time and cannot reach an internal type - it throws on the first
    /// serialize, not at startup, so an internal DTO works until the day a cache switches to MessagePack and
    /// then turns that cache into a silent miss on one deployment.
    /// </summary>
    [TestMethod]
    public void Given_CachedSnapshotTypes_When_VisibilityScanned_Then_AllArePublic()
    {
        Assert.AreEqual(Enum.GetValues<CacheKind>().Length, CachedSnapshotTypes.Length,
            "CacheKind and CachedSnapshotTypes are independent declarations of the same fact: every kind is "
            + "cached as exactly one type. Add the new kind's snapshot type here.");

        var offenders = CachedSnapshotTypes
            .Where(t => !t.IsVisible)
            .Select(t => t.FullName!)
            .ToList();

        Assert.AreEqual(0, offenders.Count,
            "these cached snapshot types are not publicly visible and cannot be serialized by the "
            + $"contractless MessagePack resolver: {string.Join(", ", offenders)}");
    }

    /// <summary>No application type takes a cache implementation in its constructor.</summary>
    [TestMethod]
    public void Given_ApplicationAssemblies_When_ConstructorsScanned_Then_NoneInjectACacheImplementation()
    {
        var offenders = new List<string>();

        foreach (var assembly in new[]
                 {
                     ApplicationContractsAssembly, ApplicationServicesAssembly, ApplicationCqrsAssembly
                 })
        {
            foreach (var type in assembly.GetTypes())
            {
                foreach (var parameter in type.GetConstructors(
                             BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                             .SelectMany(c => c.GetParameters()))
                {
                    if (ForbiddenCacheTypes.Contains(parameter.ParameterType.Name, StringComparer.Ordinal))
                        offenders.Add($"{type.FullName}.{parameter.Name}");
                }
            }
        }

        Assert.AreEqual(0, offenders.Count,
            $"application types injecting a cache implementation instead of ITypedCache: {string.Join(", ", offenders)}");
    }

    /// <summary>The application assemblies do not reference the FusionCache assembly at all.</summary>
    [TestMethod]
    public void Given_ApplicationAssemblies_When_ReferencesScanned_Then_NoneReferenceFusionCache()
    {
        var offenders = new[] { ApplicationContractsAssembly, ApplicationServicesAssembly, ApplicationCqrsAssembly }
            .Where(a => a.GetReferencedAssemblies()
                .Any(r => r.Name?.StartsWith("ZiggyCreatures", StringComparison.Ordinal) == true))
            .Select(a => a.GetName().Name)
            .ToList();

        Assert.AreEqual(0, offenders.Count,
            $"application assemblies referencing FusionCache: {string.Join(", ", offenders)}");
    }
}
