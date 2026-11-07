using EF.Testing.Environment;
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
        HostingLaneResolver.DataProtectionEncryptionKeyUrlEnvironmentVariable
    ];

    [TestMethod]
    public void Default_SelectsNonAzurePostgreSqlAndDefaultReadModel() => WithEnvironment([], () =>
    {
        Assert.AreEqual(HostingLane.NonAzure, TestHostingLane.Current.Lane);
        Assert.AreEqual(TaskFlowDbProvider.PostgreSql, TestHostingLane.DatabaseProvider);
        Assert.AreEqual("PostgreSqlJsonb", TestHostingLane.Current.ReadModel);
        Assert.IsFalse(TestHostingLane.UsesMongoDb);
    });

    [TestMethod]
    public void Azure_SelectsOnlyAzureDatabaseAndDefaultReadModel() => WithEnvironment(
        new() { [HostingLaneResolver.LaneEnvironmentVariable] = "Azure" }, () =>
        {
            Assert.AreEqual(HostingLane.Azure, TestHostingLane.Current.Lane);
            Assert.AreEqual(TaskFlowDbProvider.SqlServer, TestHostingLane.DatabaseProvider);
            Assert.AreEqual("Cosmos", TestHostingLane.Current.ReadModel);
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
        using var environment = new EnvironmentVariableScope();
        foreach (var name in azureSettings) environment.Set(name, "azure-test-value");

        WithEnvironment([], () =>
        {
            foreach (var name in azureSettings)
                Assert.IsNull(Environment.GetEnvironmentVariable(name), name);
        });
    }

    private static void WithEnvironment(Dictionary<string, string?> values, Action assertion)
    {
        using var environment = new EnvironmentVariableScope();
        foreach (var name in EnvironmentVariables) environment.Set(name, null);
        foreach (var (name, value) in values) environment.Set(name, value);
        assertion();
    }
}
