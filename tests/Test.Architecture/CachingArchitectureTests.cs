using System.Reflection;

namespace Test.Architecture;

/// <summary>
/// Rules that keep the cache behind <c>ITaskFlowCache</c>. Application code states what it wants cached and
/// what a write invalidates; expiry policy, the distributed tier, the backplane, and fail-safe live in one
/// implementation. A service reaching for FusionCache directly would set its own durations and its own keys,
/// which is how a cache starts serving two different answers for the same question.
/// Pure-unit tier: reflection over the built assemblies, no host.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class CachingArchitectureTests : BaseTest
{
    private static readonly string[] ForbiddenCacheTypes =
        ["IFusionCache", "FusionCache", "IFusionCacheProvider", "IDistributedCache", "IMemoryCache"];

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
            $"application types injecting a cache implementation instead of ITaskFlowCache: {string.Join(", ", offenders)}");
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
