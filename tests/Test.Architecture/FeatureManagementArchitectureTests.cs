using NetArchTest.Rules;

namespace Test.Architecture;

/// <summary>
/// D-042: Microsoft.FeatureManagement stays at the edges - the Api's endpoint filters (HTTP checkpoints)
/// and Infrastructure.AI's AiTaskReviewer (consumer checkpoint) - plus the Bootstrapper registration that
/// wires it up. Every other assembly in the graph, and every other namespace inside the Api itself, must
/// resolve and enforce flags through those checkpoints rather than taking a direct dependency of their own.
/// Pure-unit tier (NetArchTest only): static assembly checks; no DI, no I/O.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class FeatureManagementArchitectureTests : BaseTest
{
    private const string FeatureManagementNamespace = "Microsoft.FeatureManagement";

    /// <summary>Domain, Application, and non-AI Infrastructure assemblies must never reference Microsoft.FeatureManagement.</summary>
    [TestMethod]
    public void Given_NonEdgeAssemblies_When_DependenciesChecked_Then_NoFeatureManagementUsage()
    {
        foreach (var assembly in new[]
                 {
                     DomainModelAssembly, ApplicationContractsAssembly, ApplicationServicesAssembly,
                     ApplicationCqrsAssembly, InfrastructureDataAssembly, InfrastructureRepositoriesAssembly
                 })
        {
            var result = Types.InAssembly(assembly)
                .ShouldNot()
                .HaveDependencyOn(FeatureManagementNamespace)
                .GetResult();

            Assert.IsTrue(result.IsSuccessful,
                $"{assembly.GetName().Name} has a forbidden dependency on {FeatureManagementNamespace}: {FormatFailingTypes(result)}");
        }
    }

    /// <summary>Only TaskFlow.Api.Filters may reference Microsoft.FeatureManagement inside the Api host.</summary>
    [TestMethod]
    public void Given_ApiAssembly_When_DependenciesCheckedOutsideFilters_Then_NoFeatureManagementUsage()
    {
        var result = Types.InAssembly(ApiAssembly)
            .That()
            .DoNotResideInNamespace("TaskFlow.Api.Filters")
            .ShouldNot()
            .HaveDependencyOn(FeatureManagementNamespace)
            .GetResult();

        Assert.IsTrue(result.IsSuccessful,
            $"TaskFlow.Api has a forbidden dependency on {FeatureManagementNamespace} outside Filters: {FormatFailingTypes(result)}");
    }

    /// <summary>
    /// Sanity check that FeatureGateEndpointFilter itself does use it - without this, the boundary test
    /// above could be passing for the wrong reason (e.g. a typo in the namespace filter matching nothing).
    /// </summary>
    [TestMethod]
    public void Given_FeatureGateEndpointFilterType_When_DependenciesChecked_Then_UsesFeatureManagement()
    {
        // Internal type, not accessible via typeof/nameof from this project - matched by name string instead.
        var result = Types.InAssembly(ApiAssembly)
            .That()
            .HaveName("FeatureGateEndpointFilter")
            .Should()
            .HaveDependencyOn(FeatureManagementNamespace)
            .GetResult();

        Assert.IsTrue(result.IsSuccessful,
            "Expected TaskFlow.Api.Filters.FeatureGateEndpointFilter to depend on Microsoft.FeatureManagement " +
            "- if this fails the boundary test above may be passing for the wrong reason.");
    }

    private static string FormatFailingTypes(NetArchTest.Rules.TestResult result) =>
        result.FailingTypes != null
            ? string.Join(", ", result.FailingTypes.Select(t => t.FullName))
            : "none";
}
