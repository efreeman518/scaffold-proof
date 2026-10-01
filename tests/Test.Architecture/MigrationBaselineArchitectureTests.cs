using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using System.Reflection;
using TaskFlow.Infrastructure.Data;
using TaskFlow.Infrastructure.Data.Provider;

namespace Test.Architecture;

/// <summary>
/// D-025: TaskFlow is the baseline new apps start from, so it never carries migration history. Every DbContext has
/// exactly one initial migration per provider, and a schema change regenerates that migration instead of adding one.
/// Pure-unit tier: reflection over the two provider migration assemblies.
/// </summary>
[TestClass]
[TestCategory("Architecture")]
public class MigrationBaselineArchitectureTests : BaseTest
{
    private static readonly Type[] Contexts =
        [typeof(TaskFlowDbContextTrxn), typeof(TaskFlowFlowEngineDbContext), typeof(TaskFlowTickerQDbContext)];

    /// <summary>The provider migration assemblies, one per <see cref="TaskFlowDbProvider"/>.</summary>
    public static IEnumerable<object[]> MigrationAssemblies() =>
    [
        [TaskFlowDbProviderSelector.SqlServerMigrationsAssembly],
        [TaskFlowDbProviderSelector.PostgreSqlMigrationsAssembly],
    ];

    /// <summary>Each context has exactly one migration in each provider assembly.</summary>
    [TestMethod]
    [DynamicData(nameof(MigrationAssemblies))]
    public void Given_ProviderMigrationAssembly_When_MigrationsCounted_Then_EachContextHasExactlyOne(string assemblyName)
    {
        var migrations = Assembly.Load(assemblyName).GetTypes()
            .Where(t => typeof(Migration).IsAssignableFrom(t) && !t.IsAbstract)
            .Select(t => (Type: t, Context: t.GetCustomAttribute<DbContextAttribute>()?.ContextType))
            .ToList();

        Assert.IsFalse(migrations.Any(m => m.Context is null),
            $"{assemblyName}: every migration must name its DbContext ({string.Join(", ", migrations.Where(m => m.Context is null).Select(m => m.Type.Name))}).");
        foreach (var context in Contexts)
        {
            var names = migrations.Where(m => m.Context == context).Select(m => m.Type.Name).ToList();
            Assert.HasCount(1, names,
                $"{assemblyName}: {context.Name} must have exactly one initial migration; regenerate it instead of adding one ({string.Join(", ", names)}).");
        }

        Assert.AreEqual("InitialCreate", migrations.Single(m => m.Context == typeof(TaskFlowDbContextTrxn)).Type.Name);
        Assert.IsTrue(migrations.All(m => Contexts.Contains(m.Context)),
            $"{assemblyName}: a migration targets a context this rule does not know; add the context to {nameof(Contexts)}.");
    }
}
