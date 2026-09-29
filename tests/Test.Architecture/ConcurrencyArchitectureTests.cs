using EF.Testing.Architecture;
using Mono.Cecil;
using Mono.Cecil.Cil;
using TaskFlow.Application.Contracts.Concurrency;

namespace Test.Architecture;

/// <summary>
/// Guards the single optimistic-concurrency policy (D-032). The rule is worth an architecture test
/// rather than a code review note because one forgotten <c>SaveChangesAsync</c> silently reverts that
/// write path to last-writer-wins, and nothing else in the suite would notice.
/// Pure-unit tier (IL inspection through EF.Testing.Architecture and Mono.Cecil): no DI, I/O, or host.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class ConcurrencyArchitectureTests : BaseTest
{
    private const string RepositoryType = "EF.Data.Contracts.IRepositoryBase";
    private const string SaveChanges = "SaveChangesAsync";

    /// <summary>Verifies every Application-layer save goes through ConcurrencyGuard.SaveAsync.</summary>
    [TestMethod]
    public void Given_ApplicationAssemblies_When_SavingChanges_Then_AlwaysThroughConcurrencyGuard()
    {
        foreach (var assembly in new[] { ApplicationServicesAssembly, ApplicationCqrsAssembly })
        {
            var result = MethodCallRules.MustNotCall(assembly, RepositoryType, SaveChanges);

            Assert.IsTrue(result.IsSuccessful,
                $"{assembly.GetName().Name} calls IRepositoryBase.SaveChangesAsync directly instead of " +
                $"ConcurrencyGuard.SaveAsync, which would restore last-writer-wins on that path: {result}");
        }
    }

    /// <summary>
    /// Verifies the detector is not vacuous: ConcurrencyGuard is the one type that does call
    /// SaveChangesAsync directly, so the rule must flag it. Without this a broken scan would report
    /// every assembly clean forever. Exempting it leaves Application.Contracts clean: it is the one caller.
    /// </summary>
    [TestMethod]
    public void Given_ConcurrencyGuardItself_When_ScannedByTheRule_Then_IsFlagged()
    {
        var guard = typeof(ConcurrencyGuard).FullName!;

        var result = MethodCallRules.MustNotCall(ApplicationContractsAssembly, RepositoryType, SaveChanges);
        var exempted = MethodCallRules.MustNotCall(ApplicationContractsAssembly, RepositoryType, SaveChanges, [guard]);

        Assert.IsTrue(result.Violations.Any(v => v.StartsWith(guard + " ", StringComparison.Ordinal)),
            $"The rule must detect a direct SaveChangesAsync call; ConcurrencyGuard is the known positive. {result}");
        Assert.IsTrue(exempted.IsSuccessful, exempted.ToString());
    }

    /// <summary>
    /// Verifies the detector sees inside async methods. An <c>await</c> moves the method body into a
    /// compiler-generated nested state machine, so a rule that reads only the declaring type's own methods
    /// passes every async service. ConcurrencyGuard.SaveAsync is not async, so it cannot prove this.
    /// </summary>
    [TestMethod]
    public void Given_AnAsyncDirectSave_When_ScannedByTheRule_Then_IsFlagged()
    {
        var result = MethodCallRules.MustNotCall(typeof(AsyncDirectSaveControl).Assembly, RepositoryType, SaveChanges);

        Assert.IsTrue(
            result.Violations.Any(v => v.StartsWith(typeof(AsyncDirectSaveControl).FullName + " ", StringComparison.Ordinal)),
            $"The rule must detect a direct SaveChangesAsync call inside an async method body. {result}");
    }

    /// <summary>Verifies the guard itself still uses the throwing policy rather than ClientWins.</summary>
    [TestMethod]
    public void Given_ConcurrencyGuard_When_Inspected_Then_UsesThrowPolicy()
    {
        var guard = typeof(ConcurrencyGuard);
        var saveAsync = guard.GetMethod(nameof(ConcurrencyGuard.SaveAsync));

        Assert.IsNotNull(saveAsync, "ConcurrencyGuard.SaveAsync is the single save policy; it must exist.");

        var module = ModuleDefinition.ReadModule(guard.Assembly.Location);
        var method = module.GetType(guard.FullName)!.Methods.Single(m => m.Name == nameof(ConcurrencyGuard.SaveAsync));

        // OptimisticConcurrencyWinner.Throw is 2; ClientWins is 0 and would be a silent lost-update.
        var loadsThrow = method.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_I4_2);
        Assert.IsTrue(loadsThrow, "ConcurrencyGuard.SaveAsync must pass OptimisticConcurrencyWinner.Throw.");
    }
}
