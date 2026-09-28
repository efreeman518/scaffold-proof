using Mono.Cecil;
using Mono.Cecil.Cil;
using NetArchTest.Rules;
using TaskFlow.Application.Contracts.Concurrency;

namespace Test.Architecture;

/// <summary>
/// Guards the single optimistic-concurrency policy (D-032). The rule is worth an architecture test
/// rather than a code review note because one forgotten <c>SaveChangesAsync</c> silently reverts that
/// write path to last-writer-wins, and nothing else in the suite would notice.
/// Pure-unit tier (IL inspection through NetArchTest's Cecil model): no DI, I/O, or host.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class ConcurrencyArchitectureTests : BaseTest
{
    /// <summary>Verifies every Application-layer save goes through ConcurrencyGuard.SaveAsync.</summary>
    [TestMethod]
    public void Given_ApplicationAssemblies_When_SavingChanges_Then_AlwaysThroughConcurrencyGuard()
    {
        foreach (var assembly in new[] { ApplicationServicesAssembly, ApplicationCqrsAssembly })
        {
            var result = Types.InAssembly(assembly)
                .Should()
                .MeetCustomRule(new NoDirectSaveChangesRule())
                .GetResult();

            Assert.IsTrue(result.IsSuccessful,
                $"{assembly.GetName().Name} calls IRepositoryBase.SaveChangesAsync directly instead of " +
                $"ConcurrencyGuard.SaveAsync, which would restore last-writer-wins on that path: " +
                $"{FormatFailingTypes(result)}");
        }
    }

    /// <summary>
    /// Verifies the detector is not vacuous: ConcurrencyGuard is the one type that does call
    /// SaveChangesAsync directly, so the rule must flag it. Without this a broken scan would report
    /// every assembly clean forever.
    /// </summary>
    [TestMethod]
    public void Given_ConcurrencyGuardItself_When_ScannedByTheRule_Then_IsFlagged()
    {
        var result = Types.InAssembly(ApplicationContractsAssembly)
            .That().HaveName(nameof(ConcurrencyGuard))
            .Should()
            .MeetCustomRule(new NoDirectSaveChangesRule())
            .GetResult();

        Assert.IsFalse(result.IsSuccessful,
            "The rule must detect a direct SaveChangesAsync call; ConcurrencyGuard is the known positive.");
    }

    /// <summary>
    /// Verifies the detector sees inside async methods. An <c>await</c> moves the method body into a
    /// compiler-generated nested state machine, so a rule that reads only the declaring type's own methods
    /// passes every async service. ConcurrencyGuard.SaveAsync is not async, so it cannot prove this.
    /// </summary>
    [TestMethod]
    public void Given_AnAsyncDirectSave_When_ScannedByTheRule_Then_IsFlagged()
    {
        var result = Types.InAssembly(typeof(AsyncDirectSaveControl).Assembly)
            .That().HaveName(nameof(AsyncDirectSaveControl))
            .Should()
            .MeetCustomRule(new NoDirectSaveChangesRule())
            .GetResult();

        Assert.IsFalse(result.IsSuccessful,
            "The rule must detect a direct SaveChangesAsync call inside an async method body.");
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

    /// <summary>Formats failing types for an assertion message.</summary>
    private static string FormatFailingTypes(NetArchTest.Rules.TestResult result) =>
        result.FailingTypes != null
            ? string.Join(", ", result.FailingTypes.Select(t => t.FullName))
            : "none";

    /// <summary>
    /// Fails any type whose IL calls <c>SaveChangesAsync</c> on a repository. ConcurrencyGuard itself is
    /// the one permitted caller - it is the policy. Nested types are scanned too: async state machines and
    /// lambda closures are compiler-generated nested types that hold the real method bodies.
    /// </summary>
    private sealed class NoDirectSaveChangesRule : ICustomRule
    {
        public bool MeetsRule(TypeDefinition type)
        {
            foreach (var nested in type.NestedTypes)
            {
                if (!MeetsRule(nested)) return false;
            }

            foreach (var method in type.Methods)
            {
                if (!method.HasBody) continue;

                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is not MethodReference called) continue;
                    if (called.Name != "SaveChangesAsync") continue;

                    var declaring = called.DeclaringType?.FullName ?? string.Empty;
                    if (declaring.StartsWith("EF.Data.Contracts.IRepositoryBase", StringComparison.Ordinal))
                        return false;
                }
            }

            return true;
        }
    }
}
