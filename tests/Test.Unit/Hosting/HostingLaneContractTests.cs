using Microsoft.Extensions.Configuration;
using TaskFlow.Hosting;

namespace Test.Unit.Hosting;

[TestClass]
[TestCategory("Unit")]
[DoNotParallelize]
public sealed class HostingLaneContractTests
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
    public void Resolve_Unset_ReturnsExactAzureProfile()
    {
        var settings = Resolve();

        Assert.AreEqual(HostingLane.Azure, settings.Lane);
        Assert.AreEqual("SqlServer", settings.Database);
        Assert.AreEqual("ServiceBus", settings.Messaging);
        Assert.AreEqual("AzureBlob", settings.Storage);
        Assert.AreEqual("Cosmos", settings.ReadModel);
        Assert.AreEqual("AzureTable", settings.Audit);
        Assert.AreEqual("Sql", settings.Search);
        Assert.AreEqual("None", settings.AiServices);
        Assert.AreEqual("AzureBlob", settings.DataProtection);
    }

    [TestMethod]
    public void Resolve_NonAzure_ReturnsExactProfile()
    {
        var settings = Resolve((HostingLaneResolver.LaneConfigurationKey, "NonAzure"));

        Assert.AreEqual(HostingLane.NonAzure, settings.Lane);
        Assert.AreEqual("PostgreSql", settings.Database);
        Assert.AreEqual("RabbitMq", settings.Messaging);
        Assert.AreEqual("S3", settings.Storage);
        Assert.AreEqual("PostgreSqlJsonb", settings.ReadModel);
        Assert.AreEqual("Relational", settings.Audit);
        Assert.AreEqual("Sql", settings.Search);
        Assert.AreEqual("None", settings.AiServices);
        Assert.AreEqual("Redis", settings.DataProtection);
    }

    [TestMethod]
    public void Resolve_DeprecatedAliases_NormalizesToCanonicalValues()
    {
        var settings = Resolve(
            (HostingLaneResolver.LaneConfigurationKey, "Portable"),
            (HostingLaneResolver.ReadModelConfigurationKey, "Relational"));

        Assert.AreEqual(HostingLane.NonAzure, settings.Lane);
        Assert.AreEqual("PostgreSqlJsonb", settings.ReadModel);
    }

    [TestMethod]
    [DataRow("0")]
    [DataRow("1")]
    [DataRow("99")]
    [DataRow("OnPrem")]
    public void Resolve_NumericOrUndefinedLane_Throws(string lane)
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            Resolve((HostingLaneResolver.LaneConfigurationKey, lane)));

        StringAssert.Contains(exception.Message, lane);
        StringAssert.Contains(exception.Message, "Azure");
        StringAssert.Contains(exception.Message, "NonAzure");
    }

    [TestMethod]
    public void Resolve_SameLaneOptIns_AreAccepted()
    {
        var azure = Resolve(
            (HostingLaneResolver.SearchConfigurationKey, "AzureAiSearch"),
            (HostingLaneResolver.AiConfigurationKey, "AzureInference"));
        Assert.AreEqual("AzureAiSearch", azure.Search);
        Assert.AreEqual("AzureInference", azure.AiServices);

        var nonAzure = Resolve(
            (HostingLaneResolver.LaneConfigurationKey, "NonAzure"),
            (HostingLaneResolver.ReadModelConfigurationKey, "MongoDb"),
            (HostingLaneResolver.SearchConfigurationKey, "PgVector"),
            (HostingLaneResolver.AiConfigurationKey, "OpenAICompatible"));
        Assert.AreEqual("MongoDb", nonAzure.ReadModel);
        Assert.AreEqual("PgVector", nonAzure.Search);
        Assert.AreEqual("OpenAICompatible", nonAzure.AiServices);
    }

    [TestMethod]
    [DataRow("Azure", HostingLaneResolver.DatabaseConfigurationKey, "PostgreSql", "SqlServer")]
    [DataRow("Azure", HostingLaneResolver.MessagingConfigurationKey, "RabbitMq", "ServiceBus")]
    [DataRow("Azure", HostingLaneResolver.StorageConfigurationKey, "S3", "AzureBlob")]
    [DataRow("Azure", HostingLaneResolver.ReadModelConfigurationKey, "MongoDb", "Cosmos")]
    [DataRow("Azure", HostingLaneResolver.AuditConfigurationKey, "Relational", "AzureTable")]
    [DataRow("Azure", HostingLaneResolver.SearchConfigurationKey, "PgVector", "Sql")]
    [DataRow("Azure", HostingLaneResolver.AiConfigurationKey, "OpenAICompatible", "None")]
    [DataRow("Azure", HostingLaneResolver.DataProtectionConfigurationKey, "Redis", "AzureBlob")]
    [DataRow("NonAzure", HostingLaneResolver.DatabaseConfigurationKey, "SqlServer", "PostgreSql")]
    [DataRow("NonAzure", HostingLaneResolver.MessagingConfigurationKey, "ServiceBus", "RabbitMq")]
    [DataRow("NonAzure", HostingLaneResolver.StorageConfigurationKey, "AzureBlob", "S3")]
    [DataRow("NonAzure", HostingLaneResolver.ReadModelConfigurationKey, "Cosmos", "PostgreSqlJsonb")]
    [DataRow("NonAzure", HostingLaneResolver.AuditConfigurationKey, "AzureTable", "Relational")]
    [DataRow("NonAzure", HostingLaneResolver.SearchConfigurationKey, "AzureAiSearch", "Sql")]
    [DataRow("NonAzure", HostingLaneResolver.AiConfigurationKey, "AzureInference", "None")]
    [DataRow("NonAzure", HostingLaneResolver.DataProtectionConfigurationKey, "None", "Redis")]
    public void Resolve_CrossLaneValue_ThrowsDiagnostic(
        string lane, string setting, string configuredValue, string allowedValue)
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            Resolve(
                (HostingLaneResolver.LaneConfigurationKey, lane),
                (setting, configuredValue)));

        StringAssert.Contains(exception.Message, lane);
        StringAssert.Contains(exception.Message, setting);
        StringAssert.Contains(exception.Message, configuredValue);
        StringAssert.Contains(exception.Message, allowedValue);
    }

    [TestMethod]
    public void Resolve_UnknownProvider_ThrowsDiagnostic()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(() =>
            Resolve((HostingLaneResolver.DatabaseConfigurationKey, "Oracle")));

        StringAssert.Contains(exception.Message, "Azure");
        StringAssert.Contains(exception.Message, HostingLaneResolver.DatabaseConfigurationKey);
        StringAssert.Contains(exception.Message, "Oracle");
        StringAssert.Contains(exception.Message, "SqlServer");
    }

    [TestMethod]
    public void Resolve_EnvironmentWinsOverConfiguration()
    {
        WithEnvironment(HostingLaneResolver.LaneEnvironmentVariable, "NonAzure", () =>
        WithEnvironment(HostingLaneResolver.SearchEnvironmentVariable, "PgVector", () =>
        {
            var settings = HostingLaneResolver.Resolve(Config(
                (HostingLaneResolver.LaneConfigurationKey, "Azure"),
                (HostingLaneResolver.SearchConfigurationKey, "AzureAiSearch")));

            Assert.AreEqual(HostingLane.NonAzure, settings.Lane);
            Assert.AreEqual("PgVector", settings.Search);
        }));
    }

    [TestMethod]
    [DataRow(HostingLaneResolver.AppConfigEndpointConfigurationKey)]
    [DataRow(HostingLaneResolver.AppConfigConnectionStringConfigurationKey)]
    [DataRow(HostingLaneResolver.KeyVaultEndpointConfigurationKey)]
    [DataRow(HostingLaneResolver.KeyVaultUriConfigurationKey)]
    [DataRow(HostingLaneResolver.DataProtectionEncryptionKeyUrlConfigurationKey)]
    public void Resolve_NonAzureAzureServiceSetting_Throws(string setting)
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
            Resolve(
                (HostingLaneResolver.LaneConfigurationKey, "NonAzure"),
                (setting, "https://azure.example")));

        StringAssert.Contains(exception.Message, "NonAzure");
        StringAssert.Contains(exception.Message, setting);
        if (setting == HostingLaneResolver.AppConfigConnectionStringConfigurationKey)
            Assert.IsFalse(exception.Message.Contains("https://azure.example", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Resolve_NonAzureEmptyAzureServiceSettings_AreAllowed()
    {
        var settings = Resolve(
            (HostingLaneResolver.LaneConfigurationKey, "NonAzure"),
            (HostingLaneResolver.AppConfigEndpointConfigurationKey, " "),
            (HostingLaneResolver.KeyVaultEndpointConfigurationKey, ""),
            (HostingLaneResolver.KeyVaultUriConfigurationKey, null),
            (HostingLaneResolver.DataProtectionEncryptionKeyUrlConfigurationKey, " "));

        Assert.AreEqual(HostingLane.NonAzure, settings.Lane);
    }

    [TestMethod]
    [DataRow(HostingLaneResolver.AppConfigEndpointEnvironmentVariable, HostingLaneResolver.AppConfigEndpointConfigurationKey)]
    [DataRow(HostingLaneResolver.KeyVaultEndpointEnvironmentVariable, HostingLaneResolver.KeyVaultEndpointConfigurationKey)]
    [DataRow(HostingLaneResolver.KeyVaultUriEnvironmentVariable, HostingLaneResolver.KeyVaultUriConfigurationKey)]
    [DataRow(HostingLaneResolver.DataProtectionEncryptionKeyUrlEnvironmentVariable, HostingLaneResolver.DataProtectionEncryptionKeyUrlConfigurationKey)]
    public void ResolveFromEnvironment_NonAzureAzureServiceSetting_Throws(
        string environmentVariable, string configurationKey)
    {
        WithCleanEnvironment(() =>
        WithEnvironment(HostingLaneResolver.LaneEnvironmentVariable, "NonAzure", () =>
        WithEnvironment(environmentVariable, "https://azure.example", () =>
        {
            var exception = Assert.ThrowsExactly<InvalidOperationException>(
                HostingLaneResolver.ResolveFromEnvironment);

            StringAssert.Contains(exception.Message, "NonAzure");
            StringAssert.Contains(exception.Message, configurationKey);
            StringAssert.Contains(exception.Message, "https://azure.example");
        })));
    }

    [TestMethod]
    public void ResolveFromEnvironment_NonAzureAppConfigConnectionString_IsRedacted()
    {
        const string secret = "Endpoint=https://azure.example;Id=id;Secret=do-not-log";

        WithCleanEnvironment(() =>
        WithEnvironment(HostingLaneResolver.LaneEnvironmentVariable, "NonAzure", () =>
        WithEnvironment(HostingLaneResolver.AppConfigConnectionStringEnvironmentVariable, secret, () =>
        {
            var exception = Assert.ThrowsExactly<InvalidOperationException>(
                HostingLaneResolver.ResolveFromEnvironment);

            StringAssert.Contains(exception.Message, HostingLaneResolver.AppConfigConnectionStringConfigurationKey);
            StringAssert.Contains(exception.Message, "<redacted>");
            Assert.IsFalse(exception.Message.Contains(secret, StringComparison.Ordinal));
        })));
    }

    [TestMethod]
    [DataRow(HostingLaneResolver.AppConfigEndpointEnvironmentVariable, HostingLaneResolver.AppConfigEndpointConfigurationKey, false)]
    [DataRow(HostingLaneResolver.AppConfigConnectionStringEnvironmentVariable, HostingLaneResolver.AppConfigConnectionStringConfigurationKey, true)]
    [DataRow(HostingLaneResolver.KeyVaultEndpointEnvironmentVariable, HostingLaneResolver.KeyVaultEndpointConfigurationKey, false)]
    [DataRow(HostingLaneResolver.KeyVaultUriEnvironmentVariable, HostingLaneResolver.KeyVaultUriConfigurationKey, false)]
    [DataRow(HostingLaneResolver.DataProtectionEncryptionKeyUrlEnvironmentVariable, HostingLaneResolver.DataProtectionEncryptionKeyUrlConfigurationKey, false)]
    public void Resolve_NonAzureEnvironmentAzureServiceSetting_ThrowsWithoutEnvironmentProvider(
        string environmentVariable, string configurationKey, bool redacted)
    {
        const string value = "https://azure.example/secret";

        WithCleanEnvironment(() =>
        WithEnvironment(environmentVariable, value, () =>
        {
            var exception = Assert.ThrowsExactly<InvalidOperationException>(() =>
                HostingLaneResolver.Resolve(Config((HostingLaneResolver.LaneConfigurationKey, "NonAzure"))));

            StringAssert.Contains(exception.Message, configurationKey);
            StringAssert.Contains(exception.Message, redacted ? "<redacted>" : value);
            if (redacted) Assert.IsFalse(exception.Message.Contains(value, StringComparison.Ordinal));
        }));
    }

    [TestMethod]
    public void Resolve_NonAzureLocalEncryptionKeys_AreAllowed()
    {
        var settings = Resolve(
            (HostingLaneResolver.LaneConfigurationKey, "NonAzure"),
            ("Database:Encryption:LocalKeyBase64", "local-key"),
            ("Database:Encryption:BlindIndexKeyBase64", "local-index-key"));

        Assert.AreEqual(HostingLane.NonAzure, settings.Lane);
    }

    [TestMethod]
    public void ContainerImages_MatchCanonicalCatalog()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                "mcr.microsoft.com/mssql/server:2025-CU8-ubuntu-22.04",
                "mcr.microsoft.com/azure-messaging/servicebus-emulator:2.0.1@sha256:5a96d893b245031740f7d46e0fe5ff282d24b78c4b7d761dd57590f3f010a9b3",
                "mcr.microsoft.com/mssql/server:2022-CU27-ubuntu-22.04@sha256:4402d880dd4c34bfa7d8705e56a86cd6c88da80a1f6bbbe741f999e76264a090",
                "mcr.microsoft.com/azure-storage/azurite:3.37.0@sha256:830430c1da1a2d537e08f3e6764dd1f5ae00cf0346bcaf625b968ec3f0971fd5",
                "mcr.microsoft.com/cosmosdb/linux/azure-cosmos-emulator:vnext-EN20260907@sha256:2db1f9e74c506bcf6fc347aa937aea1c00fa756061296a5a9efba530ce86ec02",
                "pgvector/pgvector:0.8.6-pg18@sha256:2ba9ca5f2e7daa0f0e7723cba1ee9167bab54efd3640516a44ac1a928dd67e7a",
                "rabbitmq:4.3.6-management@sha256:5935b8b172f3351664b7f1610a109b3c883cec000bebeeca894d1719d18ffc76",
                "chrislusf/seaweedfs:4.47@sha256:ce9e796f1fe6f06968f4c04bdaf8f678dad9c8acdfef3d244133d71bfa6bf882",
                "mongo:8.3.11@sha256:2609aaf7a1abbff404101af896e05f243d22be742471ed857b50b9ce0270fdbd",
                "redis:8.8.2@sha256:37227fff5638322f4ebea25d6d0dc3ee50848604e82b81426f11507b3ec7d2cc"
            },
            new[]
            {
                ContainerImages.SqlServer,
                ContainerImages.ServiceBusEmulator,
                ContainerImages.ServiceBusSqlServer,
                ContainerImages.Azurite,
                ContainerImages.CosmosEmulator,
                ContainerImages.PostgreSql,
                ContainerImages.RabbitMq,
                ContainerImages.SeaweedFs,
                ContainerImages.MongoDb,
                ContainerImages.Redis
            });
    }

    private static HostingLaneSettings Resolve(params (string Key, string? Value)[] entries)
    {
        HostingLaneSettings? result = null;
        WithCleanEnvironment(() => result = HostingLaneResolver.Resolve(Config(entries)));
        return result!;
    }

    private static IConfiguration Config(params (string Key, string? Value)[] entries) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(entries.ToDictionary(entry => entry.Key, entry => entry.Value))
            .Build();

    private static void WithCleanEnvironment(Action action)
    {
        var saved = EnvironmentVariables.ToDictionary(
            name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            foreach (var name in EnvironmentVariables) Environment.SetEnvironmentVariable(name, null);
            action();
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static void WithEnvironment(string name, string value, Action action)
    {
        var original = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, value);
        try
        {
            action();
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, original);
        }
    }
}
