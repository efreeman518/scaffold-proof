namespace Test.Aspire;

[TestClass]
[TestCategory("Aspire")]
public sealed class AppHostMigratorTopologyTests
{
    [TestMethod]
    public void AppHost_DatabaseConsumersWaitForLogicalDatabaseReadiness()
    {
        var appHostSource = ReadAppHostSource();

        Assert.Contains(".WithEnvironment(\"POSTGRES_DB\", \"taskflowdb\")", appHostSource);
        Assert.DoesNotContain(".WaitFor(dbServer)", appHostSource);
        Assert.AreEqual(4, appHostSource.Split(".WaitFor(taskflowDb)", StringSplitOptions.None).Length - 1);
    }

    [TestMethod]
    public void AppHost_WiresDatabaseMigratorBeforeRuntimeHosts()
    {
        var appHostSource = ReadAppHostSource();

        Assert.Contains("TaskFlow_DatabaseMigrator", appHostSource);
        // D-020: both providers are declared, one is selected per run, and every host learns the choice.
        Assert.Contains("AddSqlServer(\"sql\"", appHostSource);
        Assert.Contains("AddPostgres(\"postgres\"", appHostSource);
        Assert.Contains(".WithEnvironment(\"Database__Provider\", dbProviderName)", appHostSource);
        Assert.Contains("connectionName: \"TaskFlowDbContextTrxn\"", appHostSource);
        Assert.Contains("connectionName: \"TaskFlowFlowEngineDbContext\"", appHostSource);
        Assert.Contains("connectionName: \"TickerQDbContext\"", appHostSource);
        Assert.Contains(".WaitForCompletion(migrator)", appHostSource);
    }

    /// <summary>
    /// D-054: the Blazor host must reach the Api's second listener. Asserted against the AppHost source
    /// and the Api's own configuration rather than a started graph, because the endpoint is derived from
    /// Kestrel configuration - Aspire creates one endpoint per Kestrel:Endpoints key, names it after the
    /// key when a scheme has more than one entry, and carries Protocols: Http2 across as transport
    /// "http2". Delete the Kestrel section and the wiring below silently resolves nothing, which is
    /// exactly the drift this pins. This lane cannot start a graph on the maintainer machine (Aspire/DCP
    /// does not work under Podman here), so a source-level assertion is the coverage that actually runs.
    /// </summary>
    [TestMethod]
    public void AppHost_WiresTheInternalGrpcReadEndpointIntoBlazor()
    {
        var appHostSource = ReadAppHostSource();

        Assert.Contains("api.GetEndpoint(\"Grpc\")", appHostSource);
        Assert.Contains(".WithReference(api.GetEndpoint(\"Grpc\"))", appHostSource);
        Assert.Contains(".WithEnvironment(\"Grpc__TaskFlowRead__Address\", api.GetEndpoint(\"Grpc\"))", appHostSource);

        // The endpoint Aspire allocates comes from this section, so it is part of the topology contract.
        var apiSettings = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Host", "TaskFlow.Api", "appsettings.json"));
        Assert.Contains("\"Grpc\"", apiSettings);
        Assert.Contains("http://+:8081", apiSettings);
        Assert.Contains("\"Protocols\": \"Http2\"", apiSettings);
    }

    [TestMethod]
    public void AppHost_PreservesFoundryLocalTestingOptInWiring()
    {
        var appHostSource = ReadAppHostSource();

        Assert.Contains("TASKFLOW_ASPIRE_ENABLE_FOUNDRY_LOCAL", appHostSource);
        Assert.Contains(".WithEnvironment(\"AiServices__DisableFoundryLocal\", \"false\")", appHostSource);
        Assert.Contains(".WithEnvironment(\"AiServices__RequireFoundryLocal\", \"true\")", appHostSource);
        Assert.Contains("TASKFLOW_FOUNDRY_LOCAL_MODEL", appHostSource);
        Assert.Contains("TASKFLOW_FOUNDRY_LOCAL_WEB_URL", appHostSource);
    }

    [TestMethod]
    public void TestAspire_UsesSingleSharedDistributedAppBuilder()
    {
        var repoRoot = FindRepoRoot();
        var testAspireRoot = Path.Combine(repoRoot, "tests", "Test.Aspire");
        var builderCall = "DistributedApplicationTestingBuilder." + "CreateAsync";
        var matches = Directory
            .EnumerateFiles(testAspireRoot, "*.cs", SearchOption.AllDirectories)
            .Select(path => new
            {
                Path = path,
                Count = File.ReadAllText(path)
                    .Split(builderCall).Length - 1
            })
            .Where(match => match.Count > 0)
            .ToArray();

        Assert.AreEqual(1, matches.Sum(match => match.Count), string.Join(Environment.NewLine, matches.Select(match => match.Path)));
        Assert.IsTrue(matches[0].Path.EndsWith(Path.Combine("Test.Aspire", "AspireTestHost.cs"), StringComparison.Ordinal), matches[0].Path);
    }

    [TestMethod]
    public void AspireHosts_UseSharedLifecycleContext()
    {
        var repoRoot = FindRepoRoot();
        var consumers = new[]
        {
            Path.Combine(repoRoot, "tests", "Test.Aspire", "AspireTestHost.cs"),
            Path.Combine(repoRoot, "tests", "Test.PlaywrightUI", "PlaywrightAspireHost.cs"),
            Path.Combine(repoRoot, "tests", "Test.PlaywrightUI", "WasmAppHost.cs")
        };

        foreach (var path in consumers)
        {
            Assert.Contains("AspireTestHostContext", File.ReadAllText(path), path);
        }
    }

    private static string ReadAppHostSource() =>
        File.ReadAllText(Path.Combine(
            FindRepoRoot(),
            "src",
            "Host",
            "Aspire",
            "AppHost",
            "AppHost.cs"));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "TaskFlow.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate repository root containing TaskFlow.slnx.");
    }
}
