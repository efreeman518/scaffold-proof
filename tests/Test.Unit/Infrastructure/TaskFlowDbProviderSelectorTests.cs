using Microsoft.Extensions.Configuration;
using TaskFlow.Infrastructure.Data.Provider;

namespace Test.Unit.Infrastructure;

/// <summary>
/// Selector-table coverage for the Database provider switch's D-035 lane fallback and fail-fast, and for
/// the D-045 PostgreSQL pooler-mode switch.
/// Pure-unit tier (in-memory IConfiguration / connection-string builder): no DI, no I/O. The env-var case
/// is marked <see cref="DoNotParallelizeAttribute"/> because <see cref="Environment.SetEnvironmentVariable(string, string?)"/>
/// is process-global.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public class TaskFlowDbProviderSelectorTests
{
    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(e => e.Key, e => e.Value))
            .Build();

    [TestMethod]
    public void Resolve_Unset_AzureLane_DefaultsToSqlServer() =>
        Assert.AreEqual(TaskFlowDbProvider.SqlServer, TaskFlowDbProviderSelector.Resolve(Config()));

    [TestMethod]
    public void Resolve_PortableLane_DefaultsToPostgreSql() =>
        Assert.AreEqual(
            TaskFlowDbProvider.PostgreSql,
            TaskFlowDbProviderSelector.Resolve(Config((HostingLaneSelector.ConfigurationKey, "Portable"))));

    [TestMethod]
    public void Resolve_ConfigBeatsLaneDefault() =>
        Assert.AreEqual(
            TaskFlowDbProvider.SqlServer,
            TaskFlowDbProviderSelector.Resolve(Config(
                (HostingLaneSelector.ConfigurationKey, "Portable"),
                (TaskFlowDbProviderSelector.ConfigurationKey, "SqlServer"))));

    [TestMethod]
    public void Resolve_UnknownValue_Throws()
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(() =>
            TaskFlowDbProviderSelector.Resolve(Config((TaskFlowDbProviderSelector.ConfigurationKey, "MySql"))));
        StringAssert.Contains(ex.Message, "SqlServer");
        StringAssert.Contains(ex.Message, "PostgreSql");
    }

    [TestMethod]
    [DoNotParallelize]
    public void Resolve_EnvWinsOverConfig()
    {
        var original = Environment.GetEnvironmentVariable(TaskFlowDbProviderSelector.EnvironmentVariable);
        Environment.SetEnvironmentVariable(TaskFlowDbProviderSelector.EnvironmentVariable, "PostgreSql");
        try
        {
            Assert.AreEqual(
                TaskFlowDbProvider.PostgreSql,
                TaskFlowDbProviderSelector.Resolve(Config((TaskFlowDbProviderSelector.ConfigurationKey, "SqlServer"))));
        }
        finally
        {
            Environment.SetEnvironmentVariable(TaskFlowDbProviderSelector.EnvironmentVariable, original);
        }
    }

    // ----- Pooler mode (D-045): config-only, no env var, no lane default. -----

    [TestMethod]
    public void PoolerModeSelector_Unset_DefaultsToNone() =>
        Assert.AreEqual(PoolerMode.None, PoolerModeSelector.Resolve(Config()));

    [TestMethod]
    public void PoolerModeSelector_Configured_ReturnsTransaction() =>
        Assert.AreEqual(
            PoolerMode.Transaction,
            PoolerModeSelector.Resolve(Config((PoolerModeSelector.ConfigurationKey, "Transaction"))));

    [TestMethod]
    public void PoolerModeSelector_UnknownValue_Throws() =>
        Assert.ThrowsExactly<ArgumentException>(() =>
            PoolerModeSelector.Resolve(Config((PoolerModeSelector.ConfigurationKey, "Session"))));

    [TestMethod]
    public void UseTaskFlowProvider_TransactionPoolerMode_AppendsNoResetAndMaxAutoPrepareFlags()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder();
        var providerOptions = new TaskFlowProviderOptions(
            TaskFlowDbProvider.PostgreSql,
            "Host=localhost;Database=TaskFlowPooler;Username=postgres;Password=NotARealPassword1!",
            "__EFMigrationsHistory",
            "taskflow",
            PoolerMode: PoolerMode.Transaction);

        options.UseTaskFlowProvider(providerOptions);
        var connectionString = ExtractConnectionString(options);
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);

        Assert.IsTrue(builder.NoResetOnClose);
        Assert.AreEqual(0, builder.MaxAutoPrepare);
    }

    [TestMethod]
    public void UseTaskFlowProvider_NonePoolerMode_LeavesNoResetOnCloseAtNpgsqlDefault()
    {
        var options = new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder();
        const string original = "Host=localhost;Database=TaskFlowPooler;Username=postgres;Password=NotARealPassword1!";
        var providerOptions = new TaskFlowProviderOptions(
            TaskFlowDbProvider.PostgreSql, original, "__EFMigrationsHistory", "taskflow");

        options.UseTaskFlowProvider(providerOptions);
        var connectionString = ExtractConnectionString(options);
        var builder = new Npgsql.NpgsqlConnectionStringBuilder(connectionString);

        // NoResetOnClose defaults to false; only the Transaction pooler mode sets it true (the meaningful
        // differentiator - MaxAutoPrepare already defaults to 0 in Npgsql, so it cannot distinguish the arms).
        Assert.IsFalse(builder.NoResetOnClose);
    }

    // FindExtension<RelationalOptionsExtension>() only matches an extension registered under that exact
    // type; Npgsql registers its own NpgsqlOptionsExtension subtype, so a base-type scan over Extensions
    // (a plain `is` check per item) is what actually finds it.
    private static string ExtractConnectionString(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder options) =>
        options.Options.Extensions
            .OfType<Microsoft.EntityFrameworkCore.Infrastructure.RelationalOptionsExtension>()
            .First()
            .ConnectionString!;
}
