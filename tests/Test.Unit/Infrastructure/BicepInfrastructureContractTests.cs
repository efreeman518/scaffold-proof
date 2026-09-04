namespace Test.Unit.Infrastructure;

/// <summary>
/// Locks the dual-provider database, Redis, container app scale-out, Service Bus topology, and Cosmos naming
/// contracts introduced for the scale-baseline infra work, and the removal of the Always Encrypted wiring.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class BicepInfrastructureContractTests
{
    [TestMethod]
    public void MainBicep_SelectsDatabaseProviderAndWiresConnectionStrings()
    {
        var main = ReadInfraFile("main.bicep");

        StringAssert.Contains(main, "param databaseProvider string");
        StringAssert.Contains(main, "@allowed(['SqlServer', 'PostgreSql'])");
        StringAssert.Contains(main, "if (databaseProvider == 'SqlServer')");
        StringAssert.Contains(main, "if (databaseProvider == 'PostgreSql')");
        StringAssert.Contains(main, "module postgres 'modules/postgres-flexible-server.bicep'");
        StringAssert.Contains(main, "module redis 'modules/redis.bicep'");
        StringAssert.Contains(main, "module redisRbac 'modules/redis-rbac.bicep'");
        StringAssert.Contains(main, "'Database__Provider', value: databaseProvider");
        StringAssert.Contains(main, "ConnectionStrings__TaskFlowDbContextQuery', value: dbReadConnectionString");
        StringAssert.Contains(main, "ConnectionStrings__Redis1', value: redis.outputs.connectionString");
        StringAssert.Contains(main, "sqlHighAvailabilityReplicaCount");
        StringAssert.Contains(main, "sqlReadScaleEnabled");
        StringAssert.Contains(main, "sqlSkuName");
    }

    [TestMethod]
    public void MainBicep_RemovesAlwaysEncryptedWiring()
    {
        var main = ReadInfraFile("main.bicep");

        Assert.IsFalse(main.Contains("enableAlwaysEncrypted", StringComparison.Ordinal));
        Assert.IsFalse(main.Contains("AlwaysEncrypted", StringComparison.Ordinal));
        Assert.IsFalse(main.Contains("keyVaultCryptoUser", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ContainerAppModule_SupportsHttpConcurrencyScaleRule()
    {
        var module = ReadInfraFile(Path.Combine("modules", "container-app.bicep"));

        StringAssert.Contains(module, "param concurrentRequests int");
        StringAssert.Contains(module, "concurrentRequests: string(concurrentRequests)");
    }

    [TestMethod]
    public void FunctionsModule_HasScaleLimitParamAndReadConnectionString()
    {
        var module = ReadInfraFile(Path.Combine("modules", "functions.bicep"));

        StringAssert.Contains(module, "param functionAppScaleLimit int = 20");
        StringAssert.Contains(module, "functionAppScaleLimit: functionAppScaleLimit");
        StringAssert.Contains(module, "ConnectionStrings__TaskFlowDbContextQuery', value: dbReadConnectionString");
    }

    [TestMethod]
    public void ServiceBusModule_ReplacesFunctionProcessorWithFilteredSubscriptions()
    {
        var module = ReadInfraFile(Path.Combine("modules", "service-bus.bicep"));

        Assert.IsFalse(module.Contains("name: 'function-processor'", StringComparison.Ordinal));
        StringAssert.Contains(module, "requiresDuplicateDetection: true");
        StringAssert.Contains(module, "duplicateDetectionHistoryTimeWindow: 'PT1H'");

        foreach (var subscriptionName in new[] { "projection", "ai-review", "workflow" })
        {
            StringAssert.Contains(module, $"name: '{subscriptionName}'");
        }

        StringAssert.Contains(module, "maxDeliveryCount: 5");
        StringAssert.Contains(module, "lockDuration: 'PT5M'");
        StringAssert.Contains(module, "deadLetteringOnFilterEvaluationExceptions: true");
        StringAssert.Contains(module, "filterType: 'SqlFilter'");
        StringAssert.Contains(module, "TaskItemCreatedEvent");
    }

    [TestMethod]
    public void RabbitMqModule_IsSingleNodeWithVolumeAndManagementPlugin()
    {
        var module = ReadInfraFile(Path.Combine("modules", "rabbitmq-container-app.bicep"));

        // Single node is load-bearing: a second replica would be an independent broker on the same file share.
        StringAssert.Contains(module, "minReplicas: 1");
        StringAssert.Contains(module, "maxReplicas: 1");
        StringAssert.Contains(module, "rabbitmq:4.1-management");
        StringAssert.Contains(module, "storageType: 'AzureFile'");
        StringAssert.Contains(module, "targetPort: 15672");
        StringAssert.Contains(module, "mountPath: '/var/lib/rabbitmq/mnesia'");
    }

    [TestMethod]
    public void MainTemplate_DeploysExactlyOneBrokerAndWiresTheProvider()
    {
        var main = ReadInfraFile("main.bicep");

        StringAssert.Contains(main, "param messagingProvider string = 'ServiceBus'");
        StringAssert.Contains(main, "modules/service-bus.bicep' = if (messagingProvider == 'ServiceBus')");
        StringAssert.Contains(main, "modules/rabbitmq-container-app.bicep' = if (messagingProvider == 'RabbitMq')");
        StringAssert.Contains(main, "ConnectionStrings__RabbitMq1");
        StringAssert.Contains(main, "name: 'Messaging__Provider', value: messagingProvider");
        StringAssert.Contains(main, "envVars: union(commonEnvVars, [");
        StringAssert.Contains(main, "], messagingEnvVars)");
    }

    [TestMethod]
    public void CosmosDbModule_NamesMatchApiAppSettings()
    {
        var module = ReadInfraFile(Path.Combine("modules", "cosmos-db.bicep"));
        var appSettings = File.ReadAllText(Path.Combine(
            FindRepoRoot(), "src", "Host", "TaskFlow.Api", "appsettings.json"));

        StringAssert.Contains(module, "name: 'taskflow-db'");
        StringAssert.Contains(module, "name: 'task-views'");
        StringAssert.Contains(appSettings, "\"DatabaseName\": \"taskflow-db\"");
        StringAssert.Contains(appSettings, "\"ContainerName\": \"task-views\"");
    }

    [TestMethod]
    public void PostgresModule_IsEntraOnlyWithPgvectorAndReadReplica()
    {
        var module = ReadInfraFile(Path.Combine("modules", "postgres-flexible-server.bicep"));

        StringAssert.Contains(module, "Microsoft.DBforPostgreSQL/flexibleServers@2025-08-01");
        StringAssert.Contains(module, "version: postgresVersion");
        StringAssert.Contains(module, "param postgresVersion string = '17'");
        StringAssert.Contains(module, "activeDirectoryAuth: 'Enabled'");
        StringAssert.Contains(module, "passwordAuth: 'Disabled'");
        StringAssert.Contains(module, "name: 'azure.extensions'");
        StringAssert.Contains(module, "value: 'VECTOR'");
        StringAssert.Contains(module, "name: 'AllowAzureServices'");
        StringAssert.Contains(module, "createMode: 'Replica'");
        StringAssert.Contains(module, "if (deployReadReplica)");
        StringAssert.Contains(module, "Maximum Pool Size=");
    }

    [TestMethod]
    public void RedisModule_IsTlsOnlyManagedRedisWithoutPrintedSecrets()
    {
        var module = ReadInfraFile(Path.Combine("modules", "redis.bicep"));

        StringAssert.Contains(module, "Microsoft.Cache/redisEnterprise@2025-07-01");
        StringAssert.Contains(module, "Microsoft.Cache/redisEnterprise/databases@2025-07-01");
        StringAssert.Contains(module, "minimumTlsVersion: '1.2'");
        StringAssert.Contains(module, "@secure()");

        var rbac = ReadInfraFile(Path.Combine("modules", "redis-rbac.bicep"));
        StringAssert.Contains(rbac, "accessPolicyAssignments@2025-07-01");
    }

    [TestMethod]
    public void ParameterFiles_ProvideDevAndProdProfiles()
    {
        var devParams = ReadInfraFile("main.dev.bicepparam");
        var prodParams = ReadInfraFile("main.prod.bicepparam");

        StringAssert.Contains(devParams, "param databaseProvider = 'SqlServer'");
        StringAssert.Contains(devParams, "minReplicas: 0");

        StringAssert.Contains(prodParams, "param sqlSkuName = 'HS_Gen5_2'");
        StringAssert.Contains(prodParams, "param sqlSkuTier = 'Hyperscale'");
        StringAssert.Contains(prodParams, "param sqlZoneRedundant = true");
        StringAssert.Contains(prodParams, "PostgreSql alternative");
        StringAssert.Contains(prodParams, "minReplicas: 2");
        StringAssert.Contains(prodParams, "maxReplicas: 100");
        StringAssert.Contains(prodParams, "concurrentRequests: 50");
    }

    private static string ReadInfraFile(string relativePath) =>
        File.ReadAllText(Path.Combine(FindRepoRoot(), "infra", relativePath));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            // A regular clone has ".git" as a directory; a git worktree checkout (used by orchestrated
            // refactor sessions) has ".git" as a plain gitdir-pointer file. Either marks the repo root.
            var gitPath = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(gitPath) || File.Exists(gitPath))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }
}
