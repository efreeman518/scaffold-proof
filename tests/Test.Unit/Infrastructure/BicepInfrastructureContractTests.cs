namespace Test.Unit.Infrastructure;

/// <summary>
/// Locks strict Azure Bicep, NonAzure Compose, Redis, container app scale-out, Service Bus topology, and Cosmos
/// naming contracts, plus removal of obsolete provider selection and Always Encrypted wiring.
/// </summary>
[TestClass]
[TestCategory("Unit")]
public sealed class BicepInfrastructureContractTests
{
    [TestMethod]
    public void MainBicep_DeploysAzureSqlAndWiresCoreConnectionStrings()
    {
        var main = ReadInfraFile("main.bicep");

        StringAssert.Contains(main, "module sqlDatabase 'modules/sql-database.bicep'");
        StringAssert.Contains(main, "module serviceBus 'modules/service-bus.bicep'");
        StringAssert.Contains(main, "module redis 'modules/redis.bicep'");
        StringAssert.Contains(main, "module redisRbac 'modules/redis-rbac.bicep'");
        StringAssert.Contains(main, "{ name: 'Database__Provider', value: 'SqlServer' }");
        StringAssert.Contains(main, "{ name: 'Messaging__Provider', value: 'ServiceBus' }");
        StringAssert.Contains(main, "ConnectionStrings__TaskFlowDbContextQuery', value: dbReadConnectionString");
        StringAssert.Contains(main, "ConnectionStrings__Redis1'");
        StringAssert.Contains(main, "sqlHighAvailabilityReplicaCount");
        StringAssert.Contains(main, "sqlReadScaleEnabled");
        StringAssert.Contains(main, "sqlSkuName");
        Assert.IsFalse(main.Contains("param databaseProvider", StringComparison.Ordinal));
        Assert.IsFalse(main.Contains("postgres-flexible-server.bicep", StringComparison.Ordinal));
        Assert.IsFalse(main.Contains("rabbitmq-container-app.bicep", StringComparison.Ordinal));
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
    public void NonAzureCompose_UsesRabbitMqWithPersistentManagementService()
    {
        var compose = File.ReadAllText(RepoRoot.Combine("deploy", "compose", "docker-compose.yml"));

        StringAssert.Contains(compose, "rabbitmq:");
        StringAssert.Contains(compose, "image: rabbitmq:4-management");
        StringAssert.Contains(compose, "RABBITMQ_DEFAULT_USER: ${RABBITMQ_DEFAULT_USER}");
        StringAssert.Contains(compose, "RABBITMQ_DEFAULT_PASS: ${RABBITMQ_DEFAULT_PASS}");
        StringAssert.Contains(compose, "- rabbitmq-data:/var/lib/rabbitmq");
        StringAssert.Contains(compose, "rabbitmq-diagnostics");
    }

    [TestMethod]
    public void MainTemplate_DeploysAzureServiceBusAndWiresManagedIdentity()
    {
        var main = ReadInfraFile("main.bicep");

        StringAssert.Contains(main, "module serviceBus 'modules/service-bus.bicep'");
        StringAssert.Contains(main, "{ name: 'Messaging__Provider', value: 'ServiceBus' }");
        StringAssert.Contains(main, "{ name: 'ServiceBus1__fullyQualifiedNamespace', value: serviceBus.outputs.namespaceEndpoint }");
        StringAssert.Contains(main, "envVars: union(commonEnvVars, [");
        StringAssert.Contains(main, "], messagingEnvVars)");
        Assert.IsFalse(main.Contains("messagingProvider", StringComparison.Ordinal));
        Assert.IsFalse(main.Contains("rabbitmq-container-app.bicep", StringComparison.Ordinal));
        Assert.IsFalse(main.Contains("ConnectionStrings__RabbitMq1", StringComparison.Ordinal));
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
    public void NonAzureCompose_UsesPgvectorPostgreSqlWithPersistentData()
    {
        var compose = File.ReadAllText(RepoRoot.Combine("deploy", "compose", "docker-compose.yml"));

        StringAssert.Contains(compose, "postgres:");
        StringAssert.Contains(compose, "image: pgvector/pgvector:pg18");
        StringAssert.Contains(compose, "POSTGRES_DB: ${POSTGRES_DB}");
        StringAssert.Contains(compose, "- postgres-data:/var/lib/postgresql/data");
        StringAssert.Contains(compose, "pg_isready");
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
    public void ParameterFiles_ProvideAzureDevAndProdProfiles()
    {
        var devParams = ReadInfraFile("main.dev.bicepparam");
        var prodParams = ReadInfraFile("main.prod.bicepparam");

        StringAssert.Contains(devParams, "param sqlSkuName = 'Basic'");
        StringAssert.Contains(devParams, "param sqlSkuTier = 'Basic'");
        StringAssert.Contains(devParams, "minReplicas: 0");

        StringAssert.Contains(prodParams, "param sqlSkuName = 'HS_Gen5_2'");
        StringAssert.Contains(prodParams, "param sqlSkuTier = 'Hyperscale'");
        StringAssert.Contains(prodParams, "param sqlZoneRedundant = true");
        StringAssert.Contains(prodParams, "minReplicas: 2");
        StringAssert.Contains(prodParams, "maxReplicas: 100");
        StringAssert.Contains(prodParams, "concurrentRequests: 50");
        Assert.IsFalse(devParams.Contains("databaseProvider", StringComparison.Ordinal));
        Assert.IsFalse(prodParams.Contains("databaseProvider", StringComparison.Ordinal));
        Assert.IsFalse(devParams.Contains("messagingProvider", StringComparison.Ordinal));
        Assert.IsFalse(prodParams.Contains("messagingProvider", StringComparison.Ordinal));
        Assert.IsFalse(devParams.Contains("PostgreSql", StringComparison.Ordinal));
        Assert.IsFalse(prodParams.Contains("PostgreSql", StringComparison.Ordinal));
        Assert.IsFalse(devParams.Contains("RabbitMq", StringComparison.Ordinal));
        Assert.IsFalse(prodParams.Contains("RabbitMq", StringComparison.Ordinal));
    }

    /// <summary>
    /// D-045: PgBouncer is an explicit NonAzure Compose profile. Transaction pooling moves clients to port
    /// 6432 and requires the pooler-safe Npgsql setting, so both deployment surfaces are pinned together.
    /// </summary>
    [TestMethod]
    public void NonAzureCompose_OffersOptInPgBouncerOnPortSixFourThreeTwo()
    {
        var compose = File.ReadAllText(RepoRoot.Combine("deploy", "compose", "docker-compose.yml"));
        var config = File.ReadAllText(RepoRoot.Combine("deploy", "compose", "pgbouncer", "pgbouncer.ini"));
        var environment = File.ReadAllText(RepoRoot.Combine("deploy", "compose", ".env.example"));

        StringAssert.Contains(compose, "pgbouncer:");
        StringAssert.Contains(compose, "profiles: [\"pooler\"]");
        StringAssert.Contains(compose, "-p 6432");
        StringAssert.Contains(config, "listen_port = 6432");
        StringAssert.Contains(config, "pool_mode = transaction");
        StringAssert.Contains(environment, "Database__PostgreSql__PoolerMode=");
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

    [TestMethod]
    public void MainBicep_DeploysBothStaticUisAndAllowsEveryUiOrigin()
    {
        var main = ReadInfraFile("main.bicep");

        StringAssert.Contains(main, "appName: '${prefix}-react'");
        StringAssert.Contains(main, "appName: '${prefix}-uno'");
        StringAssert.Contains(main, "output reactStaticWebAppName");
        StringAssert.Contains(main, "output unoStaticWebAppName");
        StringAssert.Contains(main, "CorsSettings__AllowedOrigins__1");
        StringAssert.Contains(main, "CorsSettings__AllowedOrigins__2");
        StringAssert.Contains(main, "Cors__AllowedOrigins__1");
        StringAssert.Contains(main, "Cors__AllowedOrigins__2");
    }

    private static string ReadInfraFile(string relativePath) =>
        File.ReadAllText(RepoRoot.Combine("infra", relativePath));
}
