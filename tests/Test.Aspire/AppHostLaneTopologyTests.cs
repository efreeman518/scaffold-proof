using AppHost;
using Microsoft.Extensions.Configuration;
using TaskFlow.Hosting;

namespace Test.Aspire;

/// <summary>D-060 strict-lane resolver and AppHost source contract.</summary>
[TestClass]
[TestCategory("Aspire")]
[DoNotParallelize]
public sealed class AppHostLaneTopologyTests
{
    private static readonly string[] LaneEnvironmentVariables =
    [
        HostingLaneResolver.LaneEnvironmentVariable,
        HostingLaneResolver.DatabaseEnvironmentVariable,
        HostingLaneResolver.MessagingEnvironmentVariable,
        HostingLaneResolver.StorageEnvironmentVariable,
        HostingLaneResolver.ReadModelEnvironmentVariable,
        HostingLaneResolver.AuditEnvironmentVariable,
        HostingLaneResolver.SearchEnvironmentVariable,
        HostingLaneResolver.AiEnvironmentVariable,
        HostingLaneResolver.DataProtectionEnvironmentVariable
    ];

    [TestMethod]
    public void LaneContract_MatchesSharedSelectorNames()
    {
        CollectionAssert.AreEqual(
            new[] { HostingLaneResolver.LaneEnvironmentVariable, HostingLaneResolver.LaneConfigurationKey },
            new[] { LaneDefaults.LaneEnvironmentVariable, LaneDefaults.LaneConfigurationKey });
    }

    [TestMethod]
    public void PortableAlias_NormalizesToExactNonAzureProfile()
    {
        var switches = ResolveWithLane("Portable");

        Assert.AreEqual(HostingLane.NonAzure, switches.Lane);
        Assert.AreEqual("PostgreSql", switches.Database);
        Assert.AreEqual("RabbitMq", switches.Messaging);
        Assert.AreEqual("S3", switches.Storage);
        Assert.AreEqual("PostgreSqlJsonb", switches.ReadModel);
        Assert.AreEqual("Relational", switches.Audit);
        Assert.AreEqual("Redis", switches.DataProtection);
        AssertHostEnvironmentMatches(switches);
    }

    [TestMethod]
    public void AzureLane_ResolvesExactProfile()
    {
        var switches = ResolveWithLane(lane: null);

        Assert.AreEqual(HostingLane.Azure, switches.Lane);
        Assert.AreEqual("SqlServer", switches.Database);
        Assert.AreEqual("ServiceBus", switches.Messaging);
        Assert.AreEqual("AzureBlob", switches.Storage);
        Assert.AreEqual("Cosmos", switches.ReadModel);
        Assert.AreEqual("AzureTable", switches.Audit);
        Assert.AreEqual("AzureBlob", switches.DataProtection);
        AssertHostEnvironmentMatches(switches);
    }

    [TestMethod]
    public void MongoDb_IsNonAzureOptInOnly()
    {
        var switches = ResolveWithLane("NonAzure", configuration: new()
        {
            [HostingLaneResolver.ReadModelConfigurationKey] = "MongoDb"
        });

        Assert.AreEqual("MongoDb", switches.ReadModel);
        Assert.ThrowsExactly<InvalidOperationException>(() => ResolveWithLane("Azure", configuration: new()
        {
            [HostingLaneResolver.ReadModelConfigurationKey] = "MongoDb"
        }));
    }

    [TestMethod]
    public void CrossLaneEnvironmentValue_FailsFast() =>
        Assert.ThrowsExactly<InvalidOperationException>(() =>
            ResolveWithLane("NonAzure", (HostingLaneResolver.StorageEnvironmentVariable, "AzureBlob")));

    [TestMethod]
    public void UnknownLane_FailsFastInsteadOfRunningAzure()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(() => ResolveWithLane("Vps"));
        StringAssert.Contains(exception.Message, "NonAzure");
    }

    [TestMethod]
    public void StrictGraphs_UseOnlyCatalogImagesAndGuardCrossLaneResources()
    {
        var source = ReadAppHostSource();

        foreach (var name in new[]
                 {
                     nameof(ContainerImages.SqlServer), nameof(ContainerImages.ServiceBusEmulator),
                     nameof(ContainerImages.ServiceBusSqlServer), nameof(ContainerImages.Azurite),
                     nameof(ContainerImages.CosmosEmulator), nameof(ContainerImages.PostgreSql),
                     nameof(ContainerImages.RabbitMq), nameof(ContainerImages.SeaweedFs),
                     nameof(ContainerImages.MongoDb), nameof(ContainerImages.Redis)
                 })
        {
            StringAssert.Contains(source, $"ContainerImages.{name}Repository");
            StringAssert.Contains(source, $"ContainerImages.{name}Tag");
        }

        Assert.IsFalse(source.Contains("minio", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(source.Contains("portableLane", StringComparison.Ordinal));
        StringAssert.Contains(source, "if (nonAzureLane)");
        StringAssert.Contains(source, "if (useRabbitMq)");
        StringAssert.Contains(source, "if (!nonAzureLane && (!isTesting || fullLaneAvailableInTesting))");
        StringAssert.Contains(source, "if (nonAzureLane && string.Equals(lane.ReadModel, \"MongoDb\"");
        StringAssert.Contains(source, "if (!nonAzureLane && (!isTesting || functionsAvailableInTesting || fullLaneAvailableInTesting))");
    }

    [TestMethod]
    public void BothLanes_DeclareCommonHostsAndAllUserInterfaces()
    {
        var source = ReadAppHostSource();

        foreach (var resourceName in new[]
                 {
                     "taskflowmigrator", "taskflowapi", "taskflowgateway", "taskflowscheduler",
                     "taskflowblazor", "taskflowreact", "taskflowuno"
                 })
        {
            StringAssert.Contains(source, $"\"{resourceName}\"");
        }

        StringAssert.Contains(source, ".WithReference(taskflowDb, connectionName: \"TickerQDbContext\")");
    }

    [TestMethod]
    public void Uno_ReceivesGatewayBaseUrl()
    {
        var source = ReadAppHostSource();
        var unoStart = source.IndexOf("var unoWasm =", StringComparison.Ordinal);
        Assert.IsTrue(unoStart >= 0);
        var unoBlock = source[unoStart..source.IndexOf("// The Functions host", unoStart, StringComparison.Ordinal)];
        StringAssert.Contains(unoBlock, ".WithEnvironment(\"Gateway__BaseUrl\", gateway.GetEndpoint(\"http\"))");
    }

    [TestMethod]
    public void ReadModelResources_AreReferencedOnlyWhenSelected()
    {
        var source = ReadAppHostSource();
        StringAssert.Contains(source, "if (cosmos is not null) return host.WithReference(cosmos);");
        StringAssert.Contains(source, "ConnectionStrings__MongoDb1");
        StringAssert.Contains(source, "mongoDb.GetEndpoint(\"mongodb\")");
        StringAssert.Contains(source, "functions = WithReadModel(functions);");
    }

    private static void AssertHostEnvironmentMatches(LaneSwitches switches)
    {
        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Hosting__Lane"] = switches.Lane.ToString(),
            ["Database__Provider"] = switches.Database,
            ["Messaging__Provider"] = switches.Messaging,
            ["Storage__Provider"] = switches.Storage,
            ["ReadModel__Provider"] = switches.ReadModel,
            ["Audit__Provider"] = switches.Audit,
            ["Search__Provider"] = switches.Search,
            ["AiServices__Provider"] = switches.AiServices,
            ["DataProtection__Persistence"] = switches.DataProtection
        };

        CollectionAssert.AreEquivalent(expected.Keys, switches.HostEnvironment.Keys.ToArray());
        foreach (var (key, value) in expected) Assert.AreEqual(value, switches.HostEnvironment[key], key);
    }

    private static LaneSwitches ResolveWithLane(
        string? lane,
        (string Key, string Value)? environmentOverride = null,
        Dictionary<string, string?>? configuration = null)
    {
        var saved = LaneEnvironmentVariables.ToDictionary(
            name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            foreach (var name in LaneEnvironmentVariables) Environment.SetEnvironmentVariable(name, null);
            Environment.SetEnvironmentVariable(LaneDefaults.LaneEnvironmentVariable, lane);
            if (environmentOverride is { } pair) Environment.SetEnvironmentVariable(pair.Key, pair.Value);

            return LaneDefaults.Resolve(new ConfigurationBuilder()
                .AddInMemoryCollection(configuration ?? [])
                .Build());
        }
        finally
        {
            foreach (var (name, value) in saved) Environment.SetEnvironmentVariable(name, value);
        }
    }

    private static string ReadAppHostSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TaskFlow.slnx")))
            directory = directory.Parent;

        Assert.IsNotNull(directory, "Could not locate repository root containing TaskFlow.slnx.");
        return File.ReadAllText(Path.Combine(directory.FullName, "src", "Host", "Aspire", "AppHost", "AppHost.cs"));
    }
}
