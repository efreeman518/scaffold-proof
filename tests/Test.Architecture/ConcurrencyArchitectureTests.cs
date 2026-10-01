using EF.Data.Contracts;
using Mono.Cecil;
using Mono.Cecil.Cil;
using System.Reflection;

namespace Test.Architecture;

/// <summary>
/// Guards the single optimistic-concurrency policy (D-032): every Application, Infrastructure.Repositories and
/// Scheduler save is a winner overload with <c>OptimisticConcurrencyWinner.Throw</c>. The rule is worth an
/// architecture test rather than a code review note because one policy-free or ClientWins save silently reverts that
/// write path to last-writer-wins, and nothing else in the suite would notice. EF.Testing.Architecture's
/// <c>MethodCallRules.MustNotCall</c> cannot express "call it, but only with this argument", so the rule reads
/// the IL through Mono.Cecil directly.
/// Pure-unit tier (IL inspection through Mono.Cecil): no DI, I/O, or host.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class ConcurrencyArchitectureTests : BaseTest
{
    /// <summary>Verifies every save in the Application, Infrastructure.Repositories and Scheduler assemblies uses the throwing concurrency policy.</summary>
    [TestMethod]
    public void Given_SavingAssemblies_When_SavingChanges_Then_AlwaysWithThrowPolicy()
    {
        foreach (var assembly in new[]
                 {
                     ApplicationServicesAssembly, ApplicationCqrsAssembly, InfrastructureRepositoriesAssembly, SchedulerAssembly
                 })
        {
            var failing = ThrowPolicySaveRule.FailingTypes(assembly);

            Assert.IsEmpty(failing,
                $"{assembly.GetName().Name} saves without OptimisticConcurrencyWinner.Throw, which would restore " +
                $"last-writer-wins on that path: {string.Join(", ", failing)}");
        }
    }

    /// <summary>
    /// Verifies the detector is not vacuous and sees inside async methods (an <c>await</c> moves the body into a
    /// compiler-generated state machine): policy-free, ClientWins and DBWins saves are flagged on both the
    /// repository and the DbContext shapes, a Throw save is not, and every control really contains a save call.
    /// </summary>
    [TestMethod]
    [DataRow(nameof(AsyncDirectSaveControl.SavePolicyFreeAsync), false)]
    [DataRow(nameof(AsyncDirectSaveControl.SaveClientWinsAsync), false)]
    [DataRow(nameof(AsyncDirectSaveControl.SaveThrowAsync), true)]
    [DataRow(nameof(AsyncDirectSaveControl.SaveDbContextPolicyFreeAsync), false)]
    [DataRow(nameof(AsyncDirectSaveControl.SaveDbContextClientWinsAsync), false)]
    [DataRow(nameof(AsyncDirectSaveControl.SaveDbContextDbWinsAsync), false)]
    [DataRow(nameof(AsyncDirectSaveControl.SaveDbContextThrowAsync), true)]
    public void Given_ControlSave_When_ScannedByTheRule_Then_OnlyThrowPasses(string methodName, bool expected)
    {
        var module = ModuleDefinition.ReadModule(typeof(AsyncDirectSaveControl).Assembly.Location);
        var control = module.GetType(typeof(AsyncDirectSaveControl).FullName)!;
        var stateMachine = control.NestedTypes.Single(t => t.Name.Contains($"<{methodName}>", StringComparison.Ordinal));

        Assert.IsTrue(ThrowPolicySaveRule.SaveCalls(stateMachine).Any(), $"{methodName} has no save call to scan");
        Assert.AreEqual(expected, ThrowPolicySaveRule.MeetsRule(stateMachine), methodName);
    }

    /// <summary>
    /// Fails any type whose IL calls a <c>SaveChanges</c>/<c>SaveChangesAsync</c> other than a winner overload with
    /// <c>Throw</c>, whatever declares it: <c>IRepositoryBase</c>, the repository base, or the <c>DbContext</c>
    /// itself (<c>SaveChangesAsync(winner, acceptAll = true, retries = 3, ct)</c>). The winner is the first argument,
    /// so the scan steps back over the remaining simple argument loads to reach it; any argument shape it cannot
    /// step over fails closed. App contracts that save are named for their policy (not <c>SaveChanges*</c>) and are
    /// verified by their implementation, which this rule scans. Nested types are scanned too: async state machines
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

        public static bool MeetsRule(TypeDefinition type) =>
            SaveCalls(type).All(call => HasThrowWinner(call.Instruction, call.Method));

        public static IEnumerable<(Instruction Instruction, MethodReference Method)> SaveCalls(TypeDefinition type)
        {
            foreach (var nested in type.NestedTypes)
            {
                foreach (var call in SaveCalls(nested)) yield return call;
            }

            foreach (var method in type.Methods.Where(m => m.HasBody))
            {
                foreach (var instruction in method.Body.Instructions)
                {
                    if (instruction.Operand is MethodReference { Name: "SaveChangesAsync" or "SaveChanges" } called)
                        yield return (instruction, called);
                }
            }
        }

        private static bool HasThrowWinner(Instruction call, MethodReference called)
        {
            if (called.Parameters.Count == 0
                || called.Parameters[0].ParameterType.FullName != typeof(OptimisticConcurrencyWinner).FullName)
                return false;

            var cursor = call.Previous;
            for (var i = 1; i < called.Parameters.Count && cursor is not null; i++) cursor = SkipArgument(cursor);

            return cursor is not null && TryReadInt(cursor, out var winner) && winner == ThrowWinner;
        }

        /// <summary>Steps back over the load of one simple argument (constant, argument, local, static or instance field).</summary>
        private static Instruction? SkipArgument(Instruction load)
        {
            var code = load.OpCode.Code;
            if (code == Code.Ldfld)
                return load.Previous is { } owner && IsLocalOrArgLoad(owner.OpCode.Code) ? owner.Previous : null;

            return IsLocalOrArgLoad(code) || code == Code.Ldsfld || TryReadInt(load, out _) ? load.Previous : null;
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

        private static bool IsLocalOrArgLoad(Code code) => code is
            Code.Ldarg or Code.Ldarg_S or Code.Ldarg_0 or Code.Ldarg_1 or Code.Ldarg_2 or Code.Ldarg_3 or
            Code.Ldloc or Code.Ldloc_S or Code.Ldloc_0 or Code.Ldloc_1 or Code.Ldloc_2 or Code.Ldloc_3;
    }
}
