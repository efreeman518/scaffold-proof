namespace Test.Aspire;

[TestClass]
[TestCategory("Aspire")]
public sealed class AppHostMigratorTopologyTests
{
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
