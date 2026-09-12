using AppHost;
using Microsoft.Extensions.Configuration;
using TaskFlow.Application.Contracts.Configuration;

namespace Test.Aspire;

/// <summary>
/// D-035: pins the Portable lane preset. Two layers, because this machine cannot start an Aspire graph
/// (Aspire/DCP does not work under Podman here) and the repo already forbids a second
/// <c>DistributedApplicationTestingBuilder</c> call site:
/// <list type="number">
/// <item>the resolver itself is a pure function, so the switch values and the exact environment every host
/// receives are asserted directly - that is the part a started graph would only re-derive;</item>
/// <item>the topology decisions the resolver feeds (which containers get declared) are asserted against the
/// AppHost source, the same technique <see cref="AppHostMigratorTopologyTests"/> already uses.</item>
/// </list>
/// </summary>
[TestClass]
[TestCategory("Aspire")]
public sealed class AppHostLaneTopologyTests
{
    private static readonly string[] LaneEnvironmentVariables =
    [
        LaneDefaults.LaneEnvironmentVariable,
        "TASKFLOW_DB_PROVIDER",
        "TASKFLOW_MESSAGING_PROVIDER",
        "TASKFLOW_STORAGE_PROVIDER",
        "TASKFLOW_READMODEL_PROVIDER",
        "TASKFLOW_AUDIT_PROVIDER",
        "TASKFLOW_SEARCH_PROVIDER",
        "TASKFLOW_AI_PROVIDER",
        "TASKFLOW_DATAPROTECTION_PERSISTENCE"
    ];

    /// <summary>The AppHost restates the lane contract instead of referencing it, so the names must match.</summary>
    [TestMethod]
    public void LaneContract_MatchesTheHostSideSelector()
    {
        CollectionAssert.AreEqual(
            new[] { HostingLaneSelector.EnvironmentVariable, HostingLaneSelector.ConfigurationKey },
            new[] { LaneDefaults.LaneEnvironmentVariable, LaneDefaults.LaneConfigurationKey });
        CollectionAssert.AreEqual(
            Enum.GetNames<TaskFlow.Application.Contracts.Configuration.HostingLane>(),
            Enum.GetNames<AppHost.HostingLane>());
    }

    [TestMethod]
    public void PortableLane_ResolvesPortableSwitchesAndHostEnvironment()
    {
        var switches = ResolveWithLane("Portable");

        Assert.IsTrue(switches.IsPortable);
        Assert.AreEqual("PostgreSql", switches.Database);
        Assert.AreEqual("RabbitMq", switches.Messaging);

        var expected = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Hosting__Lane"] = "Portable",
            ["Storage__Provider"] = "S3",
            ["ReadModel__Provider"] = "Relational",
            ["Audit__Provider"] = "Relational",
            // P7 flips this to PgVector together with the Bootstrapper's LaneDefaults and the artifact.
            ["Search__Provider"] = "Sql",
            ["AiServices__Provider"] = "OpenAICompatible",
            ["DataProtection__Persistence"] = "Redis"
        };

        CollectionAssert.AreEquivalent(expected.Keys, switches.HostEnvironment.Keys.ToArray());
        foreach (var (key, value) in expected)
        {
            Assert.AreEqual(value, switches.HostEnvironment[key], key);
        }
    }

    /// <summary>
    /// Unset lane must stay byte-for-byte today's graph: the AI, Search and DataProtection defaults are
    /// derived at runtime from other settings, so writing an "Azure" literal for them would change behavior.
    /// </summary>
    [TestMethod]
    public void AzureLane_WritesOnlyTheLaneItselfAndKeepsTodaysDefaults()
    {
        var switches = ResolveWithLane(lane: null);

        Assert.IsFalse(switches.IsPortable);
        Assert.AreEqual("SqlServer", switches.Database);
        Assert.AreEqual("ServiceBus", switches.Messaging);
        Assert.IsNull(switches.Storage);
        Assert.IsNull(switches.AiServices);
        Assert.IsNull(switches.DataProtection);
        CollectionAssert.AreEqual(new[] { "Hosting__Lane" }, switches.HostEnvironment.Keys.ToArray());
        Assert.AreEqual("Azure", switches.HostEnvironment["Hosting__Lane"]);
    }

    [TestMethod]
    public void SwitchEnvironmentVariable_BeatsTheLaneDefault()
    {
        var switches = ResolveWithLane("Portable", ("TASKFLOW_STORAGE_PROVIDER", "AzureBlob"));

        Assert.AreEqual("AzureBlob", switches.Storage);
        Assert.AreEqual("Relational", switches.ReadModel);
    }

    [TestMethod]
    public void SwitchConfigurationKey_BeatsTheLaneDefaultAndLosesToItsEnvironmentVariable()
    {
        var configured = ResolveWithLane("Portable", configuration: new()
        {
            ["ReadModel:Provider"] = "Cosmos"
        });
        Assert.AreEqual("Cosmos", configured.ReadModel);

        var overridden = ResolveWithLane("Portable", ("TASKFLOW_READMODEL_PROVIDER", "Relational"), configuration: new()
        {
            ["ReadModel:Provider"] = "Cosmos"
        });
        Assert.AreEqual("Relational", overridden.ReadModel);
    }

    [TestMethod]
    public void UnknownLane_FailsFastInsteadOfSilentlyRunningTheAzureGraph()
    {
        var exception = Assert.ThrowsExactly<ArgumentException>(() => ResolveWithLane("Vps"));
        StringAssert.Contains(exception.Message, "Portable");
    }

    /// <summary>
    /// The portable graph must drop every Azure-only resource and declare MinIO in their place. Asserted at
    /// source level: the guards are what decide the container set before any DCP process exists.
    /// </summary>
    [TestMethod]
    public void PortableLane_DropsAzureResourcesAndDeclaresMinio()
    {
        var source = ReadAppHostSource();

        // Postgres and RabbitMQ come from the lane defaults, and both are already declared unconditionally.
        StringAssert.Contains(source, "AddPostgres(\"postgres\"");
        StringAssert.Contains(source, "AddRabbitMQ(\"rabbitmq\")");

        // MinIO: S3 endpoint plus console, dev credentials as parameters, persistent volume outside testing.
        StringAssert.Contains(source, "AddContainer(\"minio\", \"quay.io/minio/minio\")");
        StringAssert.Contains(source, "WithHttpEndpoint(targetPort: 9000, name: \"s3\")");
        StringAssert.Contains(source, "WithHttpEndpoint(targetPort: 9001, name: \"console\")");
        StringAssert.Contains(source, "AddParameter(\"minio-access-key\"");
        StringAssert.Contains(source, "AddParameter(\"minio-secret-key\"");
        StringAssert.Contains(source, "WithVolume(\"taskflow-minio-data\", \"/data\")");

        // Every Azure-only resource is behind the lane guard.
        StringAssert.Contains(source, "if (portableLane)");
        StringAssert.Contains(source, "if (!isTesting && !portableLane)");
        StringAssert.Contains(source, "if (!portableLane && (!isTesting || functionsAvailableInTesting))");
        StringAssert.Contains(source, "var azureFoundryConfigured = !portableLane");

        // AddAzureStorage / AddAzureServiceBus stay in the else-arms, never at statement level.
        Assert.IsFalse(
            source.Contains("\nvar storage = builder.AddAzureStorage", StringComparison.Ordinal),
            "AddAzureStorage must stay inside the Azure-lane arm.");

        // The object-storage and lane-environment helpers are what keep a host from being forgotten.
        StringAssert.Contains(source, "IResourceBuilder<T> WithObjectStorage<T>");
        StringAssert.Contains(source, "IResourceBuilder<T> WithLaneEnvironment<T>");
        StringAssert.Contains(source, "Storage__S3__ServiceUrl");
        StringAssert.Contains(source, "Storage__S3__PublicServiceUrl");
        StringAssert.Contains(source, "Storage__S3__ForcePathStyle");
        StringAssert.Contains(source, "api = WithObjectStorage(api);");
        StringAssert.Contains(source, "api = WithLaneEnvironment(api);");
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
            foreach (var name in LaneEnvironmentVariables)
            {
                Environment.SetEnvironmentVariable(name, null);
            }

            Environment.SetEnvironmentVariable(LaneDefaults.LaneEnvironmentVariable, lane);
            if (environmentOverride is { } pair)
            {
                Environment.SetEnvironmentVariable(pair.Key, pair.Value);
            }

            return LaneDefaults.Resolve(new ConfigurationBuilder()
                .AddInMemoryCollection(configuration ?? [])
                .Build());
        }
        finally
        {
            foreach (var (name, value) in saved)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
        }
    }

    private static string ReadAppHostSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TaskFlow.slnx")))
        {
            directory = directory.Parent;
        }

        Assert.IsNotNull(directory, "Could not locate repository root containing TaskFlow.slnx.");
        return File.ReadAllText(Path.Combine(
            directory.FullName, "src", "Host", "Aspire", "AppHost", "AppHost.cs"));
    }
}
