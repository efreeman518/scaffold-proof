using System.ComponentModel;
using TaskFlow.Hosting;
using TaskFlow.Infrastructure.Data.Provider;
using Test.Support.Hosting;

namespace Test.Unit.Hosting;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class TestHostingLaneTests
{
    private static readonly string[] EnvironmentVariables =
    [
        HostingLaneResolver.LaneEnvironmentVariable,
        HostingLaneResolver.DatabaseEnvironmentVariable,
        HostingLaneResolver.MessagingEnvironmentVariable,
        HostingLaneResolver.StorageEnvironmentVariable,
        HostingLaneResolver.ReadModelEnvironmentVariable,
        HostingLaneResolver.AuditEnvironmentVariable,
        HostingLaneResolver.SearchEnvironmentVariable,
        HostingLaneResolver.AiEnvironmentVariable,
        HostingLaneResolver.DataProtectionEnvironmentVariable,
        HostingLaneResolver.AppConfigEndpointEnvironmentVariable,
        HostingLaneResolver.AppConfigConnectionStringEnvironmentVariable,
        HostingLaneResolver.KeyVaultEndpointEnvironmentVariable,
        HostingLaneResolver.KeyVaultUriEnvironmentVariable,
        HostingLaneResolver.DataProtectionEncryptionKeyUrlEnvironmentVariable,
        TestHostingLane.LegacyDatabaseProviderEnvironmentVariable
    ];

    [TestMethod]
    public void Default_SelectsOnlyAzureDatabaseAndDefaultReadModel() => WithEnvironment([], () =>
    {
        Assert.AreEqual(HostingLane.Azure, TestHostingLane.Current.Lane);
        Assert.AreEqual(TaskFlowDbProvider.SqlServer, TestHostingLane.DatabaseProvider);
        Assert.IsFalse(TestHostingLane.UsesMongoDb);
    });

    [TestMethod]
    public void NonAzure_SelectsPostgreSqlJsonbAndMongoOnlyWhenExplicit() => WithEnvironment(
        new() { [HostingLaneResolver.LaneEnvironmentVariable] = "NonAzure" }, () =>
        {
            Assert.AreEqual(TaskFlowDbProvider.PostgreSql, TestHostingLane.DatabaseProvider);
            Assert.AreEqual("PostgreSqlJsonb", TestHostingLane.Current.ReadModel);
            Assert.IsFalse(TestHostingLane.UsesMongoDb);
        });

    [TestMethod]
    public void NonAzureMongoSelection_IsExplicit() => WithEnvironment(new()
    {
        [HostingLaneResolver.LaneEnvironmentVariable] = "NonAzure",
        [HostingLaneResolver.ReadModelEnvironmentVariable] = "MongoDb"
    }, () => Assert.IsTrue(TestHostingLane.UsesMongoDb));

    [TestMethod]
    public void LegacyDatabaseProvider_MapsToCanonicalLane() => WithEnvironment(new()
    {
        [TestHostingLane.LegacyDatabaseProviderEnvironmentVariable] = "PostgreSql"
    }, () => Assert.AreEqual(HostingLane.NonAzure, TestHostingLane.Current.Lane));

    [TestMethod]
    public void ConflictingLegacyDatabaseProvider_FailsFast() => WithEnvironment(new()
    {
        [HostingLaneResolver.LaneEnvironmentVariable] = "NonAzure",
        [TestHostingLane.LegacyDatabaseProviderEnvironmentVariable] = "SqlServer"
    }, () => Assert.ThrowsExactly<InvalidOperationException>(() => _ = TestHostingLane.Current));

    [TestMethod]
    public void NumericLegacyDatabaseProvider_FailsFast() => WithEnvironment(new()
    {
        [TestHostingLane.LegacyDatabaseProviderEnvironmentVariable] = "1"
    }, () => Assert.ThrowsExactly<ArgumentException>(() => _ = TestHostingLane.Current));

    [TestMethod]
    public void EnvironmentIsolation_ClearsAzureServiceSettings()
    {
        var azureSettings = new[]
        {
            HostingLaneResolver.AppConfigEndpointEnvironmentVariable,
            HostingLaneResolver.AppConfigConnectionStringEnvironmentVariable,
            HostingLaneResolver.KeyVaultEndpointEnvironmentVariable,
            HostingLaneResolver.KeyVaultUriEnvironmentVariable,
            HostingLaneResolver.DataProtectionEncryptionKeyUrlEnvironmentVariable
        };
        var originals = azureSettings.ToDictionary(name => name, Environment.GetEnvironmentVariable);

        try
        {
            foreach (var name in azureSettings) Environment.SetEnvironmentVariable(name, "azure-test-value");
            WithEnvironment([], () =>
            {
                foreach (var name in azureSettings)
                    Assert.IsNull(Environment.GetEnvironmentVariable(name), name);
            });
        }
        finally
        {
            foreach (var (name, value) in originals) Environment.SetEnvironmentVariable(name, value);
        }
    }

    [TestMethod]
    public void LegacySelector_IsMarkedDeprecated()
    {
        var legacyType = typeof(TestHostingLane).Assembly.GetType("Test.Support.Hosting.TestDbProvider", throwOnError: true)!;
        Assert.IsNotNull(legacyType.GetCustomAttributes(typeof(ObsoleteAttribute), inherit: false).SingleOrDefault());
        Assert.AreEqual(EditorBrowsableState.Never,
            legacyType.GetCustomAttributes(typeof(EditorBrowsableAttribute), inherit: false)
                .Cast<EditorBrowsableAttribute>().Single().State);
    }

    private static void WithEnvironment(Dictionary<string, string?> values, Action assertion)
    {
        var originals = EnvironmentVariables.ToDictionary(name => name, Environment.GetEnvironmentVariable);
        try
        {
            foreach (var name in EnvironmentVariables) Environment.SetEnvironmentVariable(name, null);
            foreach (var (name, value) in values) Environment.SetEnvironmentVariable(name, value);
            assertion();
        }
        finally
        {
            foreach (var (name, value) in originals) Environment.SetEnvironmentVariable(name, value);
        }
    }
}
