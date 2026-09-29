using EF.Testing.Architecture;

namespace Test.Architecture;

/// <summary>
/// EF.Testing.Architecture dependency rules ensuring Infrastructure.Repositories does not depend on Application.Services or any
/// Host project - Infrastructure is allowed to know Application.Contracts but never the implementations
/// or hosts that compose them.
/// Pure-unit tier (EF.Testing.Architecture only): static assembly checks; no DI, no I/O.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class InfrastructureDependencyTests : BaseTest
{
    /// <summary>Verifies that given infrastructure repositories assembly, when dependencies checked, then no dependency on application services.</summary>
    [TestMethod]
    public void Given_InfrastructureRepositoriesAssembly_When_DependenciesChecked_Then_NoDependencyOnApplicationServices()
    {
        var result = DependencyRules.MustNotDependOn(InfrastructureRepositoriesAssembly, ["TaskFlow.Application.Services"]);

        Assert.IsTrue(result.IsSuccessful,
            $"Infrastructure.Repositories has forbidden dependency on Application.Services: {result}");
    }

    /// <summary>Verifies that given infrastructure repositories assembly, when dependencies checked, then no dependency on hosts.</summary>
    [TestMethod]
    public void Given_InfrastructureRepositoriesAssembly_When_DependenciesChecked_Then_NoDependencyOnHosts()
    {
        var result = DependencyRules.MustNotDependOn(InfrastructureRepositoriesAssembly,
            [
                "TaskFlow.Api",
                "TaskFlow.Gateway",
                "TaskFlow.Scheduler",
                "TaskFlow.Functions",
                "TaskFlow.Bootstrapper"
            ]);

        Assert.IsTrue(result.IsSuccessful,
            $"Infrastructure.Repositories has forbidden dependency on Hosts: {result}");
    }
}
