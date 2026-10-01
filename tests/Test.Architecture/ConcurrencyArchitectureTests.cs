using EF.Data.Contracts;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Reflection;

namespace Test.Architecture;

/// <summary>
/// Guards the single optimistic-concurrency policy (D-032): every Application-layer save is
/// <c>SaveChangesAsync(OptimisticConcurrencyWinner.Throw, ct)</c>. The rule is worth an architecture test
/// rather than a code review note because one policy-free or ClientWins save silently reverts that write
/// path to last-writer-wins, and nothing else in the suite would notice. EF.Testing.Architecture's
/// <c>MethodCallRules.MustNotCall</c> cannot express "call it, but only with this argument", so the rule reads
/// the IL through Mono.Cecil directly.
/// Pure-unit tier (IL inspection through Mono.Cecil): no DI, I/O, or host.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class ConcurrencyArchitectureTests : BaseTest
{
    /// <summary>Verifies every Application-layer save uses the throwing concurrency policy.</summary>
    [TestMethod]
    public void Given_ApplicationAssemblies_When_SavingChanges_Then_AlwaysWithThrowPolicy()
    {
        foreach (var assembly in new[] { ApplicationServicesAssembly, ApplicationCqrsAssembly })
        {
            var failing = ThrowPolicySaveRule.FailingTypes(assembly);

            Assert.IsEmpty(failing,
                $"{assembly.GetName().Name} saves without OptimisticConcurrencyWinner.Throw, which would restore " +
                $"last-writer-wins on that path: {string.Join(", ", failing)}");
        }
    }

    /// <summary>
    /// Verifies the detector is not vacuous and sees inside async methods (an <c>await</c> moves the body into a
    /// compiler-generated state machine): a policy-free and a ClientWins save are flagged, a Throw save is not.
    /// </summary>
    [TestMethod]
    [DataRow(nameof(AsyncDirectSaveControl.SavePolicyFreeAsync), false)]
    [DataRow(nameof(AsyncDirectSaveControl.SaveClientWinsAsync), false)]
    [DataRow(nameof(AsyncDirectSaveControl.SaveThrowAsync), true)]
    public void Given_ControlSave_When_ScannedByTheRule_Then_OnlyThrowPasses(string methodName, bool expected)
    {
        var module = ModuleDefinition.ReadModule(typeof(AsyncDirectSaveControl).Assembly.Location);
        var control = module.GetType(typeof(AsyncDirectSaveControl).FullName)!;
        var stateMachine = control.NestedTypes.Single(t => t.Name.Contains($"<{methodName}>", StringComparison.Ordinal));

        Assert.AreEqual(expected, ThrowPolicySaveRule.MeetsRule(stateMachine), methodName);
    }

    /// <summary>
    /// Fails any type whose IL calls a repository <c>SaveChangesAsync</c> other than the winner overload with
    /// <c>Throw</c>. The winner is the last integer constant loaded before the call; only the cancellation-token
    /// load (argument, local, or field) may sit between them. Nested types are scanned too: async state machines
    /// and lambda closures are compiler-generated nested types that hold the real method bodies.
    /// </summary>
    private static class ThrowPolicySaveRule
    {
        private const int ThrowWinner = (int)OptimisticConcurrencyWinner.Throw;

        public static IReadOnlyList<string> FailingTypes(Assembly assembly)
        {
            using var module = ModuleDefinition.ReadModule(assembly.Location);
            return [.. module.Types.Where(t => !MeetsRule(t)).Select(t => t.FullName)];
        }

        public static bool MeetsRule(TypeDefinition type)
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
                    if (!(called.DeclaringType?.FullName ?? string.Empty)
                            .StartsWith("EF.Data.Contracts.IRepositoryBase", StringComparison.Ordinal)) continue;

                    if (called.Parameters.Count == 0
                        || called.Parameters[0].ParameterType.FullName != typeof(OptimisticConcurrencyWinner).FullName
                        || WinnerLoadedBefore(instruction) != ThrowWinner)
                        return false;
                }
            }

            return true;
        }

        private static int? WinnerLoadedBefore(Instruction call)
        {
            for (var previous = call.Previous; previous is not null; previous = previous.Previous)
            {
                if (TryReadInt(previous, out var value)) return value;
                if (!IsTokenLoad(previous.OpCode.Code)) return null;
            }

            return null;
        }

        private static bool TryReadInt(Instruction instruction, out int value)
        {
            var code = instruction.OpCode.Code;
            if (code is >= Code.Ldc_I4_0 and <= Code.Ldc_I4_8)
            {
                value = code - Code.Ldc_I4_0;
                return true;
            }

            if (code is Code.Ldc_I4_S or Code.Ldc_I4)
            {
                value = Convert.ToInt32(instruction.Operand, System.Globalization.CultureInfo.InvariantCulture);
                return true;
            }

            value = 0;
            return false;
        }

        private static bool IsTokenLoad(Code code) => code is
            Code.Ldarg or Code.Ldarg_S or Code.Ldarg_0 or Code.Ldarg_1 or Code.Ldarg_2 or Code.Ldarg_3 or
            Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3 or
            Code.Ldfld or Code.Ldsfld;
    }
}
