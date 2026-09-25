using System.Text.Json;

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
        Assert.IsFalse(File.Exists(RepoRoot.Combine("infra", "modules", "postgres-flexible-server.bicep")));
        Assert.IsFalse(File.Exists(RepoRoot.Combine("infra", "modules", "rabbitmq-container-app.bicep")));
    }

    [TestMethod]
    public void AzureDeployment_DefaultsToSqlSearchAndGivesSchedulerCosmosAccess()
    {
        var main = ReadInfraFile("main.bicep");
        var cosmosRbacModule = ReadInfraFile(Path.Combine("modules", "cosmos-rbac.bicep"));

        StringAssert.Contains(main, "param searchProvider string = 'Sql'");
        foreach (var parameterFile in new[] { "main.bicepparam", "main.dev.bicepparam", "main.prod.bicepparam" })
        {
            StringAssert.Contains(ReadInfraFile(parameterFile), "param searchProvider = 'Sql'", parameterFile);
        }

        var scheduler = main[
            main.IndexOf("module scheduler 'modules/container-app.bicep'", StringComparison.Ordinal)..
            main.IndexOf("module blazor 'modules/container-app.bicep'", StringComparison.Ordinal)];
        StringAssert.Contains(scheduler,
            "{ name: 'ConnectionStrings__CosmosDb1', value: cosmosDb.outputs.accountEndpoint }");

        var cosmosRbac = main[
            main.IndexOf("module cosmosRbac 'modules/cosmos-rbac.bicep'", StringComparison.Ordinal)..
            main.IndexOf("module redisRbac 'modules/redis-rbac.bicep'", StringComparison.Ordinal)];
        StringAssert.Contains(cosmosRbac, "scheduler.outputs.principalId");
        StringAssert.Contains(cosmosRbacModule, "cosmosDataContributorRoleId = '00000000-0000-0000-0000-000000000002'");
    }

    [TestMethod]
    public void MainBicep_UsesExactGatewayClusterAndDestinationKeys()
    {
        var main = ReadInfraFile("main.bicep");
        var gatewaySettings = File.ReadAllText(
            RepoRoot.Combine("src", "Host", "TaskFlow.Gateway", "appsettings.json"));

        StringAssert.Contains(gatewaySettings, "\"api-cluster\"");
        StringAssert.Contains(main,
            "ReverseProxy__Clusters__api-cluster__Destinations__api__Address");
        Assert.IsFalse(main.Contains(
            "ReverseProxy__Clusters__api__Destinations__default__Address",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void AzureSqlIdentities_AreProvisionedBeforeMigrationAndRuntimeActivation()
    {
        var main = ReadInfraFile("main.bicep");
        var sql = ReadInfraFile(Path.Combine("modules", "sql-database.bicep"));
        var app = ReadInfraFile(Path.Combine("modules", "container-app.bicep"));
        var job = ReadInfraFile(Path.Combine("modules", "container-app-job.bicep"));
        var functions = ReadInfraFile(Path.Combine("modules", "functions.bicep"));
        var provisioner = ReadInfraFile(Path.Combine("scripts", "Set-AzureSqlPrincipal.ps1"));
        var workflow = File.ReadAllText(RepoRoot.Combine(".github", "workflows", "deploy.yml"));

        StringAssert.Contains(main, "module deployIdentity 'modules/deploy-identity.bicep'");
        StringAssert.Contains(main, "module migrationSqlIdentity 'modules/deploy-identity.bicep'");
        StringAssert.Contains(main, "module runtimeSqlIdentity 'modules/deploy-identity.bicep'");
        StringAssert.Contains(main, "sqlAdminPrincipalId: deployIdentity.outputs.principalId");
        StringAssert.Contains(main, "sqlAdminPrincipalType: 'Application'");
        StringAssert.Contains(main, "userAssignedIdentityId: migrationSqlIdentity.outputs.id");
        Assert.AreEqual(3, main.Split("userAssignedIdentityId: runtimeSqlIdentity.outputs.id").Length - 1);
        StringAssert.Contains(sql, "Authentication=Active Directory Managed Identity;User Id=${managedIdentityClientId}");
        StringAssert.Contains(app, "type: 'SystemAssigned, UserAssigned'");
        StringAssert.Contains(job, "type: 'UserAssigned'");
        StringAssert.Contains(functions, "type: 'SystemAssigned, UserAssigned'");

        StringAssert.Contains(provisioner, "WITH SID =");
        StringAssert.Contains(provisioner, "TYPE = E");
        StringAssert.Contains(provisioner, "ALTER ROLE [db_ddladmin]");
        StringAssert.Contains(provisioner, "GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[taskflow]");
        StringAssert.Contains(provisioner, "GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[flowengine]");
        StringAssert.Contains(provisioner, "GRANT SELECT, INSERT, UPDATE, DELETE ON SCHEMA::[scheduler]");
        StringAssert.Contains(provisioner, "$maxAttempts = 6");
        StringAssert.Contains(provisioner, "$retryDeadline = [DateTimeOffset]::UtcNow.AddMinutes(2)");
        StringAssert.Contains(provisioner, "Write-Warning \"Azure SQL provisioning attempt");
        Assert.IsFalse(provisioner.Contains("FROM EXTERNAL PROVIDER", StringComparison.Ordinal));

        var provisionMigration = workflow.IndexOf("Provision least-privilege migration identity", StringComparison.Ordinal);
        var startMigration = workflow.IndexOf("Require successful migration execution", StringComparison.Ordinal);
        var grantRuntime = workflow.IndexOf("Grant runtime access after migrations", StringComparison.Ordinal);
        var activate = workflow.IndexOf("  activate-release:", StringComparison.Ordinal);
        Assert.IsTrue(provisionMigration > 0 && provisionMigration < startMigration);
        Assert.IsTrue(startMigration < grantRuntime && grantRuntime < activate);
        StringAssert.Contains(workflow, "needs: [validate-entry, build-images, build-release, deploy-infrastructure, run-migrations]");
        StringAssert.Contains(workflow, "Keep current runtime images during provisioning");
        StringAssert.Contains(workflow, "mcr.microsoft.com/azuredocs/containerapps-helloworld:latest");
        Assert.IsFalse(workflow.Contains("SQL_ADMIN_PRINCIPAL", StringComparison.Ordinal));
    }

    [TestMethod]
    public void ContainerApps_UseSecretReferencesForRedisConnectionStrings()
    {
        var main = ReadInfraFile("main.bicep");
        var module = ReadInfraFile(Path.Combine("modules", "container-app.bicep"));

        StringAssert.Contains(module, "@secure()");
        StringAssert.Contains(module, "param secretValues object = {}");
        StringAssert.Contains(module, "secrets: containerAppSecrets");
        StringAssert.Contains(module, "env: union(envVars, secretEnvVars)");
        Assert.AreEqual(2, main.Split("secretRef: 'redis-connection'").Length - 1);
        Assert.AreEqual(2, main.Split("'redis-connection': redis.outputs.connectionString").Length - 1);
        Assert.IsFalse(main.Contains(
            "{ name: 'ConnectionStrings__Redis1', value: redis.outputs.connectionString }",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void AzureStorage_ProvisionDataProtectionContainerAndFunctionsIdentityBindingPrefix()
    {
        var storage = ReadInfraFile(Path.Combine("modules", "storage.bicep"));
        var functions = ReadInfraFile(Path.Combine("modules", "functions.bicep"));
        var main = ReadInfraFile("main.bicep");

        StringAssert.Contains(storage, "resource dataProtectionContainer");
        StringAssert.Contains(storage, "name: 'data-protection'");
        StringAssert.Contains(storage, "resource functionDeploymentContainer");
        StringAssert.Contains(storage, "name: 'function-releases'");
        StringAssert.Contains(functions, "{ name: 'BlobStorage1__blobServiceUri', value: storageBlobEndpoint }");
        StringAssert.Contains(functions, "{ name: 'BlobStorage1__queueServiceUri', value: storageQueueEndpoint }");
        StringAssert.Contains(functions, "{ name: 'BlobStorage1__credential', value: 'managedidentity' }");
        StringAssert.Contains(functions, "{ name: 'AttachmentBlobContainer', value: 'attachments' }");
        StringAssert.Contains(main, "roleDefinitionId: roles.storageBlobDataOwner");
        StringAssert.Contains(main, "roleDefinitionId: roles.storageQueueDataContributor");
        Assert.IsFalse(functions.Contains(
            "{ name: 'ConnectionStrings__BlobStorage1', value: storageBlobEndpoint }",
            StringComparison.Ordinal));
    }

    [TestMethod]
    public void FunctionsFlexConsumption_UsesDeploymentStorageRuntimeAndScaleContract()
    {
        var functions = ReadInfraFile(Path.Combine("modules", "functions.bicep"));

        StringAssert.Contains(functions, "functionAppConfig: {");
        StringAssert.Contains(functions, "deployment: {");
        StringAssert.Contains(functions, "value: functionDeploymentContainerUri");
        StringAssert.Contains(functions, "type: 'SystemAssignedIdentity'");
        StringAssert.Contains(functions, "name: 'dotnet-isolated'");
        StringAssert.Contains(functions, "version: '10.0'");
        StringAssert.Contains(functions, "maximumInstanceCount: functionAppScaleLimit");
        StringAssert.Contains(functions, "instanceMemoryMB: 2048");
        Assert.IsFalse(functions.Contains("functionAppScaleLimit: functionAppScaleLimit", StringComparison.Ordinal));
        Assert.IsFalse(functions.Contains("FUNCTIONS_EXTENSION_VERSION", StringComparison.Ordinal));
        Assert.IsFalse(functions.Contains("FUNCTIONS_WORKER_RUNTIME", StringComparison.Ordinal));
        Assert.IsFalse(functions.Contains("ftpsState", StringComparison.Ordinal));
    }

    /// <summary>D-064: every app that serves traffic drains readiness on shutdown; the migrator job does not.</summary>
    [TestMethod]
    public void ContainerApps_ServingHostsDrainOnShutdown_MigratorDoesNot()
    {
        var main = ReadInfraFile("main.bicep");
        StringAssert.Contains(main, "{ name: 'Hosting__DrainDelaySeconds', value: '5' }");
        foreach (var app in new[] { "gateway", "api", "scheduler", "blazor" })
        {
            var start = main.IndexOf($"module {app} 'modules/container-app.bicep'", StringComparison.Ordinal);
            Assert.IsTrue(start >= 0, app);
            var envVars = main.IndexOf("envVars: union(", start, StringComparison.Ordinal);
            StringAssert.StartsWith(main[(envVars + "envVars: union(".Length)..], "servingEnvVars,", app);
        }

        var migrator = main.IndexOf("module migrator 'modules/container-app-job.bicep'", StringComparison.Ordinal);
        var migratorEnv = main.IndexOf("envVars: union(", migrator, StringComparison.Ordinal);
        StringAssert.StartsWith(main[(migratorEnv + "envVars: union(".Length)..], "commonEnvVars,");

        var compiled = ReadInfraFile("main.json");
        Assert.AreEqual(4, compiled.Split("Hosting__DrainDelaySeconds").Length - 1,
            "the compiled template must be rebuilt: one inlined drain setting per serving app");
    }

    [TestMethod]
    public void CompiledFunctionsFlexConsumption_HasRequiredSemanticStructure()
    {
        using var template = JsonDocument.Parse(ReadInfraFile("main.json"));
        var configs = FindProperties(template.RootElement, "functionAppConfig").ToArray();

        Assert.AreEqual(1, configs.Length);
        var config = configs[0];
        var storage = config.GetProperty("deployment").GetProperty("storage");
        Assert.AreEqual("blobContainer", storage.GetProperty("type").GetString());
        Assert.AreEqual(
            "SystemAssignedIdentity",
            storage.GetProperty("authentication").GetProperty("type").GetString());

        var runtime = config.GetProperty("runtime");
        Assert.AreEqual("dotnet-isolated", runtime.GetProperty("name").GetString());
        Assert.AreEqual("10.0", runtime.GetProperty("version").GetString());
        Assert.AreEqual(2048, config.GetProperty("scaleAndConcurrency").GetProperty("instanceMemoryMB").GetInt32());
    }

    [TestMethod]
    public void AzureBootstrap_UpdatesExistingFederatedCredentialWithoutHidingLookupFailures()
    {
        var bootstrap = ReadInfraFile(Path.Combine("scripts", "bootstrap.ps1"));
        var list = bootstrap.IndexOf("az identity federated-credential list", StringComparison.Ordinal);
        var listExitCheck = bootstrap.IndexOf(
            "if ($LASTEXITCODE -ne 0) { throw \"Failed to list federated credentials\" }",
            StringComparison.Ordinal);
        var update = bootstrap.IndexOf("az identity federated-credential update", StringComparison.Ordinal);
        var create = bootstrap.IndexOf("az identity federated-credential create", StringComparison.Ordinal);
        var mutationExitCheck = bootstrap.IndexOf(
            "if ($LASTEXITCODE -ne 0) { throw \"Failed to create or update federated credential\" }",
            StringComparison.Ordinal);

        Assert.IsTrue(list >= 0 && list < listExitCheck);
        Assert.IsTrue(listExitCheck < update && update < mutationExitCheck);
        Assert.IsTrue(listExitCheck < create && create < mutationExitCheck);
        StringAssert.Contains(bootstrap, "$credentialExists");
    }

    [TestMethod]
    public void AzureBootstrap_EstablishesNarrowSubscriptionAccessBeforeEnvironmentOidcTrust()
    {
        var bootstrap = ReadInfraFile(Path.Combine("scripts", "bootstrap.ps1"));
        var foundation = ReadInfraFile(Path.Combine("bootstrap", "deploy-identity-foundation.bicep"));
        var workflow = File.ReadAllText(RepoRoot.Combine(".github", "workflows", "deploy.yml"));

        StringAssert.Contains(bootstrap, "[string]$GitHubEnvironment = 'dev'");
        StringAssert.Contains(bootstrap,
            "'--subject', \"repo:${GitHubRepo}:environment:$GitHubEnvironment\"");
        StringAssert.Contains(workflow, "environment: dev");
        Assert.IsFalse(bootstrap.Contains("GitHubBranch", StringComparison.Ordinal));
        Assert.IsFalse(bootstrap.Contains("ref:refs/heads", StringComparison.Ordinal));

        var foundationDeployment = bootstrap.IndexOf(
            "bootstrap/deploy-identity-foundation.bicep", StringComparison.Ordinal);
        var federatedCredential = bootstrap.IndexOf(
            "az identity federated-credential list", StringComparison.Ordinal);

        Assert.IsTrue(foundationDeployment >= 0 && foundationDeployment < federatedCredential);
        Assert.IsFalse(bootstrap.Contains("../main.bicep", StringComparison.Ordinal));
        Assert.IsFalse(bootstrap.Contains("gatewayFqdn", StringComparison.Ordinal));

        Assert.AreEqual(1, foundation.Split("'Microsoft.Resources/deployments/*'").Length - 1);
        Assert.AreEqual(1,
            foundation.Split("'Microsoft.Resources/subscriptions/resourceGroups/read'").Length - 1);
        Assert.AreEqual(1,
            foundation.Split("'Microsoft.Resources/subscriptions/resourceGroups/write'").Length - 1);
        StringAssert.Contains(foundation, "roleDefinitionId: subscriptionDeploymentRole.id");
        StringAssert.Contains(foundation, "principalId: deployIdentity.outputs.principalId");
        StringAssert.Contains(foundation, "module deployContributor '../modules/role-assignment.bicep'");
        StringAssert.Contains(foundation, "module deployUaa '../modules/role-assignment.bicep'");
        Assert.AreEqual(3, foundation.Split("scope: rg").Length - 1);

        var customRole = foundation[
            foundation.IndexOf("resource subscriptionDeploymentRole", StringComparison.Ordinal)..
            foundation.IndexOf("resource subscriptionDeploymentAssignment", StringComparison.Ordinal)];
        Assert.IsFalse(customRole.Contains("b24988ac-6180-42a0-ab88-20f7382dd24c", StringComparison.Ordinal));
        Assert.IsFalse(customRole.Contains("18d7d88d-d35e-4fb5-a5c3-7773c20a72d9", StringComparison.Ordinal));
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
    public void SchedulerWithoutIngress_StaysWarmInDefaultAndDeploymentProfiles()
    {
        var main = ReadInfraFile("main.bicep");
        var defaultProfile = main[
            main.IndexOf("param schedulerProfile object", StringComparison.Ordinal)..
            main.IndexOf("@description('Blazor container app scale/sizing profile')", StringComparison.Ordinal)];
        var scheduler = main[
            main.IndexOf("module scheduler 'modules/container-app.bicep'", StringComparison.Ordinal)..
            main.IndexOf("module blazor 'modules/container-app.bicep'", StringComparison.Ordinal)];
        var dev = ReadInfraFile("main.dev.bicepparam");
        var devProfile = dev[
            dev.IndexOf("param schedulerProfile", StringComparison.Ordinal)..
            dev.IndexOf("param blazorProfile", StringComparison.Ordinal)];
        var prod = ReadInfraFile("main.prod.bicepparam");
        var prodProfile = prod[
            prod.IndexOf("param schedulerProfile", StringComparison.Ordinal)..
            prod.IndexOf("param blazorProfile", StringComparison.Ordinal)];

        StringAssert.Contains(scheduler, "ingressEnabled: false");
        StringAssert.Contains(scheduler, "minReplicas: schedulerProfile.minReplicas");
        StringAssert.Contains(defaultProfile, "minReplicas: 1");
        StringAssert.Contains(devProfile, "minReplicas: 1");
        StringAssert.Contains(prodProfile, "minReplicas: 2");
        Assert.IsFalse(defaultProfile.Contains("minReplicas: 0", StringComparison.Ordinal));
        Assert.IsFalse(devProfile.Contains("minReplicas: 0", StringComparison.Ordinal));
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
        StringAssert.Contains(module, "maximumInstanceCount: functionAppScaleLimit");
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
        StringAssert.Contains(compose, "image: rabbitmq:4.3.6-management@sha256:5935b8b172f3351664b7f1610a109b3c883cec000bebeeca894d1719d18ffc76");
        StringAssert.Contains(compose, "RABBITMQ_DEFAULT_USER: ${RABBITMQ_DEFAULT_USER:");
        StringAssert.Contains(compose, "RABBITMQ_DEFAULT_PASS: ${RABBITMQ_DEFAULT_PASS:");
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
        StringAssert.Contains(compose, "image: pgvector/pgvector:0.8.6-pg18@sha256:2ba9ca5f2e7daa0f0e7723cba1ee9167bab54efd3640516a44ac1a928dd67e7a");
        StringAssert.Contains(compose, "POSTGRES_DB: ${POSTGRES_DB:");
        StringAssert.Contains(compose, "- postgres-data:/var/lib/postgresql");
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

    private static IEnumerable<JsonElement> FindProperties(JsonElement element, string propertyName)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals(propertyName))
                {
                    yield return property.Value;
                }

                foreach (var descendant in FindProperties(property.Value, propertyName))
                {
                    yield return descendant;
                }
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                foreach (var descendant in FindProperties(item, propertyName))
                {
                    yield return descendant;
                }
            }
        }
    }
}
