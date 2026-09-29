using EF.Testing.Architecture;

namespace Test.Architecture;

/// <summary>
/// EF.Testing.Architecture dependency rules pinning the Domain.Model assembly as a leaf dependency: it must not reference
/// Application (Contracts/Services/Mappers/Models/MessageHandlers), Infrastructure, EF Core, or any
/// Host project.
/// Pure-unit tier (EF.Testing.Architecture only): assembly-metadata inspection, no infra. Anything heavier is wasted  - 
/// these are pure dependency-direction assertions.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class DomainDependencyTests : BaseTest
{
    /// <summary>Verifies that given domain model assembly, when dependencies checked, then no dependency on application.</summary>
    [TestMethod]
    public void Given_DomainModelAssembly_When_DependenciesChecked_Then_NoDependencyOnApplication()
    {
        var result = DependencyRules.MustNotDependOn(DomainModelAssembly,
            [
                "TaskFlow.Application.Contracts",
                "TaskFlow.Application.Services",
                "TaskFlow.Application.Mappers",
                "TaskFlow.Application.Models",
                "TaskFlow.Application.MessageHandlers"
            ]);

        Assert.IsTrue(result.IsSuccessful,
            $"Domain.Model has forbidden dependency on Application: {result}");
    }

    /// <summary>Verifies that given domain model assembly, when dependencies checked, then no dependency on infrastructure.</summary>
    [TestMethod]
    public void Given_DomainModelAssembly_When_DependenciesChecked_Then_NoDependencyOnInfrastructure()
    {
        var result = DependencyRules.MustNotDependOn(DomainModelAssembly,
            [
                "TaskFlow.Infrastructure.Data",
                "TaskFlow.Infrastructure.Repositories",
                "Microsoft.EntityFrameworkCore"
            ]);

        Assert.IsTrue(result.IsSuccessful,
            $"Domain.Model has forbidden dependency on Infrastructure: {result}");
    }

    /// <summary>Verifies that given domain model assembly, when dependencies checked, then no dependency on hosts.</summary>
    [TestMethod]
    public void Given_DomainModelAssembly_When_DependenciesChecked_Then_NoDependencyOnHosts()
    {
        var result = DependencyRules.MustNotDependOn(DomainModelAssembly,
            [
                "TaskFlow.Api",
                "TaskFlow.Gateway",
                "TaskFlow.Scheduler",
                "TaskFlow.Functions",
                "TaskFlow.Bootstrapper"
            ]);

        Assert.IsTrue(result.IsSuccessful,
            $"Domain.Model has forbidden dependency on Hosts: {result}");
    }
}
