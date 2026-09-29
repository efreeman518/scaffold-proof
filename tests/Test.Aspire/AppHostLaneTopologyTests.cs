using AppHost;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using EF.Testing.Environment;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
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

    private static readonly string[] GraphEnvironmentVariables =
    [
        .. LaneEnvironmentVariables,
        "TASKFLOW_ASPIRE_TESTING",
        "TASKFLOW_ASPIRE_FULL_LANE",
        "TASKFLOW_ASPIRE_FUNCTIONS_AVAILABLE",
        "TASKFLOW_ASPIRE_REACT_AVAILABLE",
        "TASKFLOW_ASPIRE_UNO_WASM_AVAILABLE",
        "ConnectionStrings__chat",
        "ConnectionStrings:chat",
        "AiServices__Provider",
        "AiServices:Provider",
        "AiServices__FoundryEndpoint",
        "AiServices:FoundryEndpoint",
        "AiServices__AgentModelDeployment",
        "AiServices:AgentModelDeployment"
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
    public void UnsetLane_ResolvesExactNonAzureProfile()
    {
        var switches = ResolveWithLane(lane: null);

        Assert.AreEqual(HostingLane.NonAzure, switches.Lane);
        CollectionAssert.AreEquivalent(
            ResolveWithLane("NonAzure").HostEnvironment.ToArray(), switches.HostEnvironment.ToArray());
        AssertHostEnvironmentMatches(switches);
    }

    [TestMethod]
    public void AzureLane_ResolvesExactProfile()
    {
        var switches = ResolveWithLane("Azure");

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
    public void UnknownLane_FailsFastInsteadOfRunningDefaultLane()
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
        StringAssert.Contains(source, "if (!isTesting || reactAvailableInTesting || fullLaneAvailableInTesting)");
        StringAssert.Contains(source, "if (!isTesting || unoWasmAvailableInTesting || fullLaneAvailableInTesting)");
        StringAssert.Contains(source,
            ".WithEnvironment(\"RateLimiting__Tiers__standard__PermitLimit\", \"10000\")");
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
        StringAssert.Contains(source, "if (cosmos is not null) return host.WithReference(cosmos).WaitFor(cosmos);");
        StringAssert.Contains(source, ".WithEnvironment(\"DOTNET_ENVIRONMENT\", \"Testing\")");
        StringAssert.Contains(source, ".WithEnvironment(\"ASPNETCORE_ENVIRONMENT\", \"Testing\")");
        StringAssert.Contains(source, "Testing__UseNoOpCosmosReadModel");
        StringAssert.Contains(source, "ConnectionStrings__MongoDb1");
        StringAssert.Contains(source, "mongoDb.GetEndpoint(\"mongodb\")");
        StringAssert.Contains(source, "functions = WithReadModel(functions);");
    }

    [TestMethod]
    public void Scheduler_ReceivesSelectedObjectStoreInBothLanes()
    {
        var source = ReadAppHostSource();
        StringAssert.Contains(source, "scheduler = WithObjectStorage(scheduler);");
        StringAssert.Contains(source, "return host.WithReference(blobs!).WaitFor(storage!);");
        StringAssert.Contains(source, ".WaitFor(seaweedFs);");
        StringAssert.Contains(source, ".WithHttpEndpoint(targetPort: 9333, name: \"master\")");
        StringAssert.Contains(source, ".WithHttpHealthCheck(path: \"/cluster/status\", endpointName: \"master\")");
    }

    [TestMethod]
    public void RabbitMqHosts_ReceivePackageConnectionString()
    {
        var source = ReadAppHostSource();
        StringAssert.Contains(source,
            ".WithEnvironment(\"Messaging__RabbitMq__ConnectionString\", rabbitMq.Resource.ConnectionStringExpression)");
    }

    [TestMethod]
    public async Task FullLane_AzureGraph_ContainsEveryHostAndNoNonAzureResource()
    {
        var resources = (await BuildResourceGraphAsync("Azure")).ResourceNames;

        AssertPresent(resources, "sql", "taskflowdb", "redis", "AzureStorage", "BlobStorage1",
            "TableStorage1", "ServiceBus1", "ServiceBus1-mssql", "CosmosDb1", "taskflowmigrator",
            "taskflowapi", "taskflowgateway", "taskflowscheduler", "taskflowblazor", "taskflowreact",
            "taskflowuno", "taskflowfunctions");
        AssertAbsent(resources, "postgres", "rabbitmq", "seaweedfs", "mongodb");
    }

    [TestMethod]
    public async Task FullLane_NonAzureGraph_ContainsEveryCommonHostAndNoAzureResource() =>
        AssertNonAzureTopology((await BuildResourceGraphAsync("NonAzure")).ResourceNames);

    /// <summary>D-060: a plain dotnet run of the AppHost with no lane set brings up the NonAzure topology.</summary>
    [TestMethod]
    public async Task FullLane_UnsetLaneGraph_IsNonAzureTopology() =>
        AssertNonAzureTopology((await BuildResourceGraphAsync(lane: null)).ResourceNames);

    private static void AssertNonAzureTopology(IReadOnlySet<string> resources)
    {
        AssertPresent(resources, "postgres", "taskflowdb", "redis", "rabbitmq", "seaweedfs",
            "taskflowmigrator", "taskflowapi", "taskflowgateway", "taskflowscheduler", "taskflowblazor",
            "taskflowreact", "taskflowuno");
        AssertAbsent(resources, "sql", "AzureStorage", "BlobStorage1", "TableStorage1", "ServiceBus1",
            "ServiceBus1-mssql", "CosmosDb1", "mongodb", "taskflowfunctions");
    }

    [TestMethod]
    public async Task FullLane_NonAzureMongoOptIn_AddsOnlyMongoResource()
    {
        var resources = (await BuildResourceGraphAsync("NonAzure", readModel: "MongoDb")).ResourceNames;

        AssertPresent(resources, "mongodb");
        AssertAbsent(resources, "AzureStorage", "BlobStorage1", "TableStorage1", "ServiceBus1",
            "ServiceBus1-mssql", "CosmosDb1", "taskflowfunctions");
    }

    [TestMethod]
    public async Task AzureManifestMode_BuildsWithoutEmulatorSidecarLookupFailure()
    {
        var resources = (await BuildResourceGraphAsync("Azure", manifestMode: true)).ResourceNames;

        AssertPresent(resources, "ServiceBus1", "taskflowfunctions", "taskflowreact", "taskflowuno");
        AssertAbsent(resources, "ServiceBus1-mssql", "rabbitmq", "seaweedfs");
    }

    /// <summary>
    /// The resolved provider is the only switch: with AzureInference selected, every host that registers the AI
    /// client gets that provider and the chat connection together, so no host can resolve None beside a wired
    /// Foundry connection or AzureInference without one.
    /// </summary>
    [TestMethod]
    [TestCategory("AzureFoundry")]
    [DataRow(true, false)]
    [DataRow(false, true)]
    public async Task AzureFoundry_ProviderWithEndpointAndDeployment_GivesHostsProviderAndConnectionTogether(
        bool providerFromEnvironment, bool providerFromConfiguration)
    {
        var graph = await BuildResourceGraphAsync(
            "Azure",
            aiProvider: providerFromEnvironment ? "AzureInference" : null,
            providerConfiguration: providerFromConfiguration ? "AzureInference" : null,
            foundryEndpoint: "https://taskflow.services.ai.azure.com/",
            foundryDeployment: "chat-deployment");

        AssertAbsent(graph.ResourceNames, "chat");
        foreach (var host in new[] { "taskflowapi", "taskflowfunctions" })
        {
            var environment = graph.Environment[host];
            Assert.AreEqual("AzureInference", environment["AiServices__Provider"], host);
            Assert.AreEqual(
                "Endpoint=https://taskflow.services.ai.azure.com/;Deployment=chat-deployment",
                environment["ConnectionStrings__chat"],
                host);
        }
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public async Task AzureFoundry_ProviderWithCompleteConnection_AddsConnectionResource()
    {
        var graph = await BuildResourceGraphAsync(
            "Azure",
            aiProvider: "AzureInference",
            chatConnection: "Endpoint=https://taskflow.services.ai.azure.com/;Deployment=chat-deployment");

        AssertPresent(graph.ResourceNames, "chat", "taskflowapi");
        Assert.AreEqual("AzureInference", graph.Environment["taskflowapi"]["AiServices__Provider"]);
        Assert.IsTrue(graph.Environment["taskflowapi"].ContainsKey("ConnectionStrings__chat"));
    }

    [TestMethod]
    public async Task NoFoundrySettings_DefaultProvider_WiresNoChatConnection()
    {
        var graph = await BuildResourceGraphAsync("Azure");

        AssertAbsent(graph.ResourceNames, "chat");
        Assert.AreEqual("None", graph.Environment["taskflowapi"]["AiServices__Provider"]);
        Assert.IsFalse(graph.Environment["taskflowapi"].ContainsKey("ConnectionStrings__chat"));
    }

    /// <summary>
    /// Foundry settings without the provider used to wire the chat connection while the hosts resolved None and
    /// ignored it. The graph now refuses to build and names both sides of the mismatch.
    /// </summary>
    [TestMethod]
    [TestCategory("AzureFoundry")]
    [DataRow("Azure")]
    [DataRow("NonAzure")]
    public async Task AzureFoundry_SettingsWithoutAzureInferenceProvider_FailsNamingSettingsAndProvider(string lane)
    {
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BuildResourceGraphAsync(
                lane,
                foundryEndpoint: "https://taskflow.services.ai.azure.com/",
                foundryDeployment: "chat-deployment"));

        StringAssert.Contains(exception.Message, "AiServices:FoundryEndpoint, AiServices:AgentModelDeployment");
        StringAssert.Contains(exception.Message, HostingLaneResolver.AiConfigurationKey);
        StringAssert.Contains(exception.Message, HostingLaneResolver.AiEnvironmentVariable);
        StringAssert.Contains(exception.Message, $"'None' on the {lane} lane");
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public async Task AzureFoundry_ConnectionWithoutAzureInferenceProvider_FailsNamingSettingsAndProvider()
    {
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BuildResourceGraphAsync(
                "Azure",
                chatConnection: "Endpoint=https://taskflow.services.ai.azure.com/;Deployment=chat-deployment"));

        StringAssert.Contains(exception.Message, "(ConnectionStrings:chat)");
        StringAssert.Contains(exception.Message, "'None' on the Azure lane");
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public async Task AzureFoundry_ProviderWithEndpointWithoutDeployment_FailsWithConfigurationError()
    {
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BuildResourceGraphAsync(
                "Azure",
                aiProvider: "AzureInference",
                foundryEndpoint: "https://taskflow.services.ai.azure.com/"));

        StringAssert.Contains(exception.Message, "non-empty Deployment");
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public async Task AzureFoundry_ProviderWithNonHttpsEndpoint_FailsWithConfigurationError()
    {
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BuildResourceGraphAsync(
                "Azure",
                aiProvider: "AzureInference",
                foundryEndpoint: "http://taskflow.example/",
                foundryDeployment: "chat-deployment"));

        StringAssert.Contains(exception.Message, "absolute HTTPS Endpoint");
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public async Task AzureFoundry_ProviderWithConnectionWithoutDeployment_FailsWithConfigurationError()
    {
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BuildResourceGraphAsync(
                "Azure",
                aiProvider: "AzureInference",
                chatConnection: "Endpoint=https://taskflow.services.ai.azure.com/"));

        StringAssert.Contains(exception.Message, "non-empty Deployment");
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public async Task AzureFoundry_ProviderOnly_FailsWithConfigurationError()
    {
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BuildResourceGraphAsync("Azure", aiProvider: "AzureInference"));

        StringAssert.Contains(exception.Message, "absolute HTTPS Endpoint");
    }

    [TestMethod]
    [TestCategory("AzureFoundry")]
    public async Task AzureFoundry_ProviderWithDeploymentOnly_FailsWithConfigurationError()
    {
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BuildResourceGraphAsync("Azure", aiProvider: "AzureInference", foundryDeployment: "chat-deployment"));

        StringAssert.Contains(exception.Message, "absolute HTTPS Endpoint");
    }
    [TestMethod]
    public async Task NonAzure_AzureInferenceProvider_RemainsRejected()
    {
        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            BuildResourceGraphAsync("NonAzure", aiProvider: "AzureInference"));

        StringAssert.Contains(exception.Message, "NonAzure");
    }

    [TestMethod]
    public async Task FullLane_AzureContainerImages_UseSingleRegistryAndCanonicalTags()
    {
        var images = (await BuildResourceGraphAsync("Azure")).ContainerImages;

        foreach (var expected in new[]
                 {
                     ContainerImages.SqlServer,
                     ContainerImages.ServiceBusEmulator,
                     ContainerImages.ServiceBusSqlServer,
                     ContainerImages.Azurite,
                     ContainerImages.CosmosEmulator
                 })
        {
            Assert.IsTrue(images.Contains(expected),
                $"missing {expected}; actual images: {string.Join(", ", images.Order())}");
        }

        Assert.IsFalse(images.Any(image =>
                image.Contains("mcr.microsoft.com/mcr.microsoft.com/", StringComparison.Ordinal)),
            $"duplicated registry in: {string.Join(", ", images.Order())}");
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
        using var environment = new EnvironmentVariableScope();
        foreach (var name in LaneEnvironmentVariables) environment.Set(name, null);
        environment.Set(LaneDefaults.LaneEnvironmentVariable, lane);
        if (environmentOverride is { } pair) environment.Set(pair.Key, pair.Value);

        return LaneDefaults.Resolve(new ConfigurationBuilder()
            .AddInMemoryCollection(configuration ?? [])
            .Build());
    }

    private static string ReadAppHostSource()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TaskFlow.slnx")))
            directory = directory.Parent;

        Assert.IsNotNull(directory, "Could not locate repository root containing TaskFlow.slnx.");
        return File.ReadAllText(Path.Combine(directory.FullName, "src", "Host", "Aspire", "AppHost", "AppHost.cs"));
    }

    private static async Task<AppHostGraph> BuildResourceGraphAsync(
        string? lane,
        string? readModel = null,
        bool manifestMode = false,
        string? foundryEndpoint = null,
        string? foundryDeployment = null,
        string? chatConnection = null,
        string? aiProvider = null,
        string? providerConfiguration = null)
    {
        using var environment = new EnvironmentVariableScope();
        foreach (var name in GraphEnvironmentVariables) environment.Set(name, null);
        environment.Set(HostingLaneResolver.LaneEnvironmentVariable, lane);
        environment.Set(HostingLaneResolver.ReadModelEnvironmentVariable, readModel);
        environment.Set(HostingLaneResolver.AiEnvironmentVariable, aiProvider);
        environment.Set("TASKFLOW_ASPIRE_TESTING", "true");
        environment.Set("TASKFLOW_ASPIRE_FULL_LANE", "true");
        environment.Set("AiServices__FoundryEndpoint", foundryEndpoint);
        environment.Set("AiServices__AgentModelDeployment", foundryDeployment);
        environment.Set("ConnectionStrings__chat", chatConnection);

        var args = new List<string>
        {
            $"--AiServices:Provider={providerConfiguration ?? string.Empty}",
            $"--AiServices:FoundryEndpoint={foundryEndpoint ?? string.Empty}",
            $"--AiServices:AgentModelDeployment={foundryDeployment ?? string.Empty}",
            $"--ConnectionStrings:chat={chatConnection ?? string.Empty}"
        };
        if (manifestMode) args.AddRange(["--publisher", "manifest"]);

        var programType = Type.GetType("Program, AppHost", throwOnError: true)!;
        var builder = await DistributedApplicationTestingBuilder.CreateAsync(
            programType,
            args: [.. args],
            configureBuilder: (appOptions, _) => appOptions.DisableDashboard = true);

        var resourceNames = builder.Resources.Select(resource => resource.Name).ToHashSet(StringComparer.Ordinal);
        var containerImages = builder.Resources
            .Select(resource => resource.TryGetContainerImageName(out var imageName) ? imageName : null)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

        // Publish-mode values: references render as manifest expressions, so nothing needs an allocated endpoint.
        var hostEnvironment = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (var host in builder.Resources.OfType<IResourceWithEnvironment>()
                     .Where(resource => resource.Name is "taskflowapi" or "taskflowfunctions"))
        {
            var configuration = await ExecutionConfigurationBuilder.Create(host)
                .WithEnvironmentVariablesConfig()
                .BuildAsync(
                    new DistributedApplicationExecutionContext(DistributedApplicationOperation.Publish),
                    NullLogger.Instance,
                    CancellationToken.None);
            if (configuration.Exception is not null) throw configuration.Exception;
            hostEnvironment[host.Name] = configuration.EnvironmentVariables
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        }

        return new(resourceNames, containerImages, hostEnvironment);
    }

    private static void AssertPresent(IReadOnlySet<string> resources, params string[] expected)
    {
        foreach (var resource in expected)
            Assert.IsTrue(resources.Contains(resource),
                $"missing {resource}; actual resources: {string.Join(", ", resources.Order())}");
    }

    private static void AssertAbsent(IReadOnlySet<string> resources, params string[] expected)
    {
        foreach (var resource in expected)
            Assert.IsFalse(resources.Contains(resource),
                $"unexpected {resource}; actual resources: {string.Join(", ", resources.Order())}");
    }

    private sealed record AppHostGraph(
        HashSet<string> ResourceNames,
        HashSet<string> ContainerImages,
        IReadOnlyDictionary<string, Dictionary<string, string>> Environment);
}
