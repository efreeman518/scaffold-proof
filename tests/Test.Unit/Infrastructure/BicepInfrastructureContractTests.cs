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

    /// <summary>
    /// D-049: the container-app module must carry all three probes on the contract paths, and affinity must be
    /// opt-in. A probe pointed at an authenticated or aggregate route is the failure this locks out - the
    /// platform probes anonymously, so only the /healthz* routes can ever answer it.
    /// </summary>
    [TestMethod]
    public void ContainerAppModule_HasLiveReadyStartupProbesAndOptInAffinity()
    {
        var module = ReadInfraFile(Path.Combine("modules", "container-app.bicep"));

        StringAssert.Contains(module, "param readinessPath string = '/healthz/ready'");
        StringAssert.Contains(module, "param livenessPath string = '/healthz/live'");
        StringAssert.Contains(module, "param startupPath string = '/healthz/live'");
        StringAssert.Contains(module, "type: 'Startup'");
        StringAssert.Contains(module, "type: 'Liveness'");
        StringAssert.Contains(module, "type: 'Readiness'");
        StringAssert.Contains(module, "@allowed(['sticky', 'none'])");
        StringAssert.Contains(module, "param stickySessions string = 'none'");
        StringAssert.Contains(module, "affinity: stickySessions");
        Assert.IsFalse(module.Contains("/readyz", StringComparison.Ordinal));

        // Blazor Server is the only host with per-connection server state, so it is the only sticky app, and
        // no app may override a probe path away from the contract.
        var main = ReadInfraFile("main.bicep");
        StringAssert.Contains(main, "stickySessions: 'sticky'");
        Assert.AreEqual(1, main.Split("stickySessions:").Length - 1);
        Assert.IsFalse(main.Contains("readinessPath:", StringComparison.Ordinal));
        Assert.IsFalse(main.Contains("livenessPath:", StringComparison.Ordinal));
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
        var appSettings = File.ReadAllText(
            RepoRoot.Combine("src", "Host", "TaskFlow.Api", "appsettings.json"));

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

    /// <summary>
    /// D-045: PgBouncer is a server parameter on Flexible Server, not a sidecar, so enabling it moves the
    /// client port to 6432 and requires the app to switch to pooler-safe Npgsql settings. Both halves come
    /// from one flag; this pins that they cannot drift apart, and that the Burstable limitation stays written
    /// down where someone setting the flag will read it.
    /// </summary>
    [TestMethod]
    public void PostgresModule_SupportsOptInPgBouncerOnPortSixFourThreeTwo()
    {
        var module = ReadInfraFile(Path.Combine("modules", "postgres-flexible-server.bicep"));

        StringAssert.Contains(module, "param pgBouncerEnabled bool = false");
        StringAssert.Contains(module, "name: 'pgbouncer.enabled'");
        StringAssert.Contains(module, "if (pgBouncerEnabled)");
        StringAssert.Contains(module, "var pgPort = pgBouncerEnabled ? 6432 : 5432");
        StringAssert.Contains(module, "Port=${pgPort};");
        StringAssert.Contains(module, "Burstable");
        Assert.IsFalse(module.Contains("Port=5432;", StringComparison.Ordinal));

        var main = ReadInfraFile("main.bicep");
        StringAssert.Contains(main, "param postgresPgBouncerEnabled bool = false");
        StringAssert.Contains(main, "pgBouncerEnabled: postgresPgBouncerEnabled");
        StringAssert.Contains(main, "name: 'Database__PostgreSql__PoolerMode'");
        StringAssert.Contains(main, "postgresPgBouncerEnabled ? 'Transaction' : 'None'");
    }

    /// <summary>
    /// The Blazor host throws at startup when its gateway address is missing, and the template used to set a
    /// name nothing binds. Pinned against the source that reads it so the two can only be renamed together.
    /// </summary>
    [TestMethod]
    public void MainBicep_SetsTheGatewayAddressUnderTheKeyBlazorReads()
    {
        var blazorProgram = File.ReadAllText(
            RepoRoot.Combine("src", "UI", "TaskFlow.Blazor", "Program.cs"));
        StringAssert.Contains(blazorProgram, "Configuration[\"Gateway:BaseUrl\"]");

        var main = ReadInfraFile("main.bicep");
        StringAssert.Contains(main, "{ name: 'Gateway__BaseUrl', value: 'https://${gateway.outputs.fqdn}' }");
        Assert.IsFalse(main.Contains("name: 'ApiBaseUrl'", StringComparison.Ordinal));
    }

    private static string ReadInfraFile(string relativePath) =>
        File.ReadAllText(RepoRoot.Combine("infra", relativePath));
}
