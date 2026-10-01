using EF.Testing.Architecture;

namespace Test.Architecture;

/// <summary>
/// EF.Testing.Architecture dependency rules guarding the Application layer's outbound dependencies - Application.Contracts and
/// Application.Services must not reference Infrastructure (EF, repositories) or any Host project.
/// Pure-unit tier (EF.Testing.Architecture only): runs on loaded <see cref="System.Reflection.Assembly"/> metadata
/// with no DI, I/O, or test host. A heavier tier would not exercise more of the rule - these are static
/// architectural invariants.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class ApplicationDependencyTests : BaseTest
{
    /// <summary>Verifies that given application contracts assembly, when dependencies checked, then no dependency on infrastructure.</summary>
    [TestMethod]
    public void Given_ApplicationContractsAssembly_When_DependenciesChecked_Then_NoDependencyOnInfrastructure()
    {
        var result = DependencyRules.MustNotDependOn(ApplicationContractsAssembly,
            [
                "TaskFlow.Infrastructure.Data",
                "TaskFlow.Infrastructure.Repositories",
                "Microsoft.EntityFrameworkCore"
            ]);

        Assert.IsTrue(result.IsSuccessful,
            $"Application.Contracts has forbidden dependency on Infrastructure: {result}");
    }

    /// <summary>Verifies that given application contracts assembly, when dependencies checked, then no dependency on hosts.</summary>
    [TestMethod]
    public void Given_ApplicationContractsAssembly_When_DependenciesChecked_Then_NoDependencyOnHosts()
    {
        var result = DependencyRules.MustNotDependOn(ApplicationContractsAssembly,
            [
                "TaskFlow.Api",
                "TaskFlow.Gateway",
                "TaskFlow.Scheduler",
                "TaskFlow.Functions",
                "TaskFlow.Bootstrapper"
            ]);

        Assert.IsTrue(result.IsSuccessful,
            $"Application.Contracts has forbidden dependency on Hosts: {result}");
    }

    /// <summary>Verifies that given application services assembly, when dependencies checked, then no dependency on infrastructure.</summary>
    [TestMethod]
    public void Given_ApplicationServicesAssembly_When_DependenciesChecked_Then_NoDependencyOnInfrastructure()
    {
        var result = DependencyRules.MustNotDependOn(ApplicationServicesAssembly,
            [
                "TaskFlow.Infrastructure.Data",
                "TaskFlow.Infrastructure.Repositories",
                "Microsoft.EntityFrameworkCore"
            ]);

        Assert.IsTrue(result.IsSuccessful,
            $"Application.Services has forbidden dependency on Infrastructure: {result}");
    }

    /// <summary>Verifies that given application services assembly, when dependencies checked, then no dependency on hosts.</summary>
    [TestMethod]
    public void Given_ApplicationServicesAssembly_When_DependenciesChecked_Then_NoDependencyOnHosts()
    {
        var result = DependencyRules.MustNotDependOn(ApplicationServicesAssembly,
            [
                "TaskFlow.Api",
                "TaskFlow.Gateway",
                "TaskFlow.Scheduler",
                "TaskFlow.Functions",
                "TaskFlow.Bootstrapper"
            ]);

        Assert.IsTrue(result.IsSuccessful,
            $"Application.Services has forbidden dependency on Hosts: {result}");
    }
}
