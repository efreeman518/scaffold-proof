// ============================================================================
// TaskFlow Dev Environment - Main Bicep
// Single resource group, minimal SKUs, managed identities, Entra-only auth
// ============================================================================

targetScope = 'subscription'

// ---- Parameters ----

@description('Resource name prefix')
param resourcePrefix string = 'taskflow'

@description('Environment name')
param environmentName string = 'dev'

@description('Azure region')
param location string = 'eastus2'

@description('SQL Entra admin principal ID')
param sqlAdminPrincipalId string

@description('SQL Entra admin principal name')
param sqlAdminPrincipalName string

@description('SQL Entra admin principal type')
@allowed(['User', 'Group', 'Application'])
param sqlAdminPrincipalType string = 'User'

@description('Gateway container image')
param gatewayImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

@description('API container image')
param apiImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

@description('Scheduler container image')
param schedulerImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

@description('Database migrator container image')
param migratorImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

@description('Blazor container image')
param blazorImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

@description('Active database provider')
@allowed(['SqlServer', 'PostgreSql'])
param databaseProvider string = 'SqlServer'

@description('Messaging transport: Azure Service Bus (default) or a single-node RabbitMQ container app (D-034)')
@allowed([
  'ServiceBus'
  'RabbitMq'
])
param messagingProvider string = 'ServiceBus'

@description('RabbitMQ broker password, used only when messagingProvider is RabbitMq')
@secure()
param rabbitMqPassword string = ''

@description('Explicit connection pool ceiling emitted in every connection string')
param dbMaxPoolSize int = 100

@description('SQL Server database SKU name, e.g. Basic (dev) or HS_Gen5_2 (prod Hyperscale)')
param sqlSkuName string = 'Basic'

@description('SQL Server database SKU tier, e.g. Basic (dev) or Hyperscale (prod)')
param sqlSkuTier string = 'Basic'

@description('SQL Server database SKU family, required for Hyperscale (e.g. Gen5); empty for DTU tiers')
param sqlSkuFamily string = ''

@description('SQL Server database SKU capacity (vCores for Hyperscale); 0 leaves the ARM default')
param sqlSkuCapacity int = 0

@description('SQL Hyperscale zone redundancy (prod)')
param sqlZoneRedundant bool = false

@description('SQL Hyperscale HA secondary replica count (prod); 0 disables HA replicas')
param sqlHighAvailabilityReplicaCount int = 0

@description('SQL Hyperscale read-scale routing via ApplicationIntent=ReadOnly (prod)')
param sqlReadScaleEnabled bool = false

@description('PostgreSQL Flexible Server compute SKU name')
param pgSkuName string = 'Standard_B1ms'

@description('PostgreSQL Flexible Server compute SKU tier')
param pgSkuTier string = 'Burstable'

@description('PostgreSQL Flexible Server storage size in GB')
param pgStorageSizeGB int = 32

@description('PostgreSQL high availability mode: ZoneRedundant for prod, Disabled for dev')
@allowed(['Disabled', 'ZoneRedundant'])
param pgHighAvailabilityMode string = 'Disabled'

@description('Deploy a PostgreSQL prod-only read replica')
param pgDeployReadReplica bool = false

@description('Redis Enterprise SKU name: small Balanced tier for dev, HA tier for prod')
param redisSkuName string = 'Balanced_B0'

@description('Redis Enterprise high availability (prod)')
param redisHighAvailability bool = false

@description('Flex Consumption Functions app maximum instance count')
param functionAppScaleLimit int = 20

@description('Gateway container app scale/sizing profile')
param gatewayProfile object = {
  minReplicas: 0
  maxReplicas: 2
  concurrentRequests: 0
  cpu: '0.25'
  memory: '0.5Gi'
}

@description('API container app scale/sizing profile')
param apiProfile object = {
  minReplicas: 0
  maxReplicas: 3
  concurrentRequests: 0
  cpu: '0.5'
  memory: '1Gi'
}

@description('Scheduler container app scale/sizing profile')
param schedulerProfile object = {
  minReplicas: 0
  maxReplicas: 1
  concurrentRequests: 0
  cpu: '0.25'
  memory: '0.5Gi'
}

@description('Blazor container app scale/sizing profile')
param blazorProfile object = {
  minReplicas: 0
  maxReplicas: 1
  concurrentRequests: 0
  cpu: '0.25'
  memory: '0.5Gi'
}

// ---- Variables ----

var prefix = '${resourcePrefix}-${environmentName}'
var tags = {
  environment: environmentName
  project: 'taskflow'
  managedBy: 'bicep'
}

// Well-known RBAC role definition IDs
var roles = {
  // Storage
  storageBlobDataContributor: 'ba92f5b4-2d11-453d-a403-e96b0029c9fe'
  storageTableDataContributor: '0a9a7e1f-b9d0-4cc4-a60d-0319b160aaa3'
  // Cosmos DB
  cosmosDbDataContributor: '00000000-0000-0000-0000-000000000002' // Built-in Cosmos data role
  // Service Bus
  serviceBusDataSender: '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39'
  serviceBusDataReceiver: '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0'
  // Key Vault
  keyVaultSecretsUser: '4633458b-17de-408a-b874-0445c86b69e6'
  // App Configuration
  appConfigDataReader: '516239f1-63e1-4d78-a4de-a74fb236a071'
  // Contributor (for deploy identity)
  contributor: 'b24988ac-6180-42a0-ab88-20f7382dd24c'
  userAccessAdministrator: '18d7d88d-d35e-4fb5-a5c3-7773c20a72d9'
}

// ---- Resource Group ----

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: '${prefix}-rg'
  location: location
  tags: tags
}

// ---- Foundation Modules ----

module logAnalytics 'modules/log-analytics.bicep' = {
  name: 'logAnalytics'
  scope: rg
  params: {
    resourcePrefix: prefix
    location: location
    tags: tags
  }
}

// Single shared, workspace-based Application Insights resource for all TaskFlow hosts. Its connection
// string is fanned out below; hosts export via the Azure Monitor OpenTelemetry distro in ServiceDefaults.
module appInsights 'modules/app-insights.bicep' = {
  name: 'appInsights'
  scope: rg
  params: {
    resourcePrefix: prefix
    location: location
    logAnalyticsWorkspaceId: logAnalytics.outputs.id
    tags: tags
  }
}

module containerAppsEnv 'modules/container-apps-environment.bicep' = {
  name: 'containerAppsEnv'
  scope: rg
  params: {
    resourcePrefix: prefix
    location: location
    logAnalyticsWorkspaceId: logAnalytics.outputs.id
    tags: tags
  }
}

module keyVault 'modules/key-vault.bicep' = {
  name: 'keyVault'
  scope: rg
  params: {
    resourcePrefix: prefix
    location: location
    tags: tags
  }
}

module appConfig 'modules/app-configuration.bicep' = {
  name: 'appConfig'
  scope: rg
  params: {
    resourcePrefix: prefix
    location: location
    tags: tags
  }
}

// ---- Data Modules ----

module sqlDatabase 'modules/sql-database.bicep' = if (databaseProvider == 'SqlServer') {
  name: 'sqlDatabase'
  scope: rg
  params: {
    resourcePrefix: prefix
    location: location
    sqlAdminPrincipalId: sqlAdminPrincipalId
    sqlAdminPrincipalName: sqlAdminPrincipalName
    sqlAdminPrincipalType: sqlAdminPrincipalType
    skuName: sqlSkuName
    skuTier: sqlSkuTier
    skuFamily: sqlSkuFamily
    skuCapacity: sqlSkuCapacity
    zoneRedundant: sqlZoneRedundant
    highAvailabilityReplicaCount: sqlHighAvailabilityReplicaCount
    readScaleEnabled: sqlReadScaleEnabled
    maxPoolSize: dbMaxPoolSize
    tags: tags
  }
}

// Postgres Entra admin/principal params reuse the SQL admin params (one DB admin identity regardless of engine).
// 'Application' (SQL's term) maps to Postgres' 'ServicePrincipal'.
module postgres 'modules/postgres-flexible-server.bicep' = if (databaseProvider == 'PostgreSql') {
  name: 'postgres'
  scope: rg
  params: {
    resourcePrefix: prefix
    location: location
    pgAdminPrincipalId: sqlAdminPrincipalId
    pgAdminPrincipalName: sqlAdminPrincipalName
    pgAdminPrincipalType: any(sqlAdminPrincipalType == 'Application' ? 'ServicePrincipal' : sqlAdminPrincipalType)
    skuName: pgSkuName
    skuTier: pgSkuTier
    storageSizeGB: pgStorageSizeGB
    highAvailabilityMode: pgHighAvailabilityMode
    deployReadReplica: pgDeployReadReplica
    maxPoolSize: dbMaxPoolSize
    // Deterministic name, not a module output - avoids a circular dependency with the api container app module.
    entraConnectingPrincipalName: '${prefix}-api'
    tags: tags
  }
}

module redis 'modules/redis.bicep' = {
  name: 'redis'
  scope: rg
  params: {
    resourcePrefix: prefix
    location: location
    skuName: redisSkuName
    highAvailability: redisHighAvailability
    tags: tags
  }
}

module cosmosDb 'modules/cosmos-db.bicep' = {
  name: 'cosmosDb'
  scope: rg
  params: {
    resourcePrefix: prefix
    location: location
    tags: tags
  }
}

module serviceBus 'modules/service-bus.bicep' = if (messagingProvider == 'ServiceBus') {
  name: 'serviceBus'
  scope: rg
  params: {
    resourcePrefix: prefix
    location: location
    tags: tags
  }
}

// D-034: exactly one broker is deployed. RabbitMQ here is the dev/staging proof of the second transport;
// production would point ConnectionStrings__RabbitMq1 at a managed broker or a cluster (infra/README.md).
module rabbitMq 'modules/rabbitmq-container-app.bicep' = if (messagingProvider == 'RabbitMq') {
  name: 'rabbitMq'
  scope: rg
  params: {
    resourcePrefix: prefix
    location: location
    environmentId: containerAppsEnv.outputs.id
    environmentName: containerAppsEnv.outputs.name
    storageAccountName: storage.outputs.appStorageName
    brokerPassword: rabbitMqPassword
    tags: tags
  }
}

// D-054: the Api's second listener. It is the container port declared in appsettings Kestrel:Endpoints:Grpc,
// the additional ingress port mapping below, and the port Blazor dials - one constant so the three cannot
// drift apart.
var apiGrpcPort = 8081

var rabbitMqConnectionString = messagingProvider == 'RabbitMq'
  ? 'amqp://taskflow:${rabbitMqPassword}@${rabbitMq!.outputs.host}:5672/'
  : ''

// Every host gets the same provider choice plus the connection value the chosen transport reads.
var messagingEnvVars = messagingProvider == 'RabbitMq'
  ? [
      { name: 'Messaging__Provider', value: messagingProvider }
      { name: 'ConnectionStrings__RabbitMq1', value: rabbitMqConnectionString }
      { name: 'Messaging__RabbitMq__ConnectionString', value: rabbitMqConnectionString }
    ]
  : [
      { name: 'Messaging__Provider', value: messagingProvider }
      { name: 'SERVICEBUS__fullyQualifiedNamespace', value: serviceBus!.outputs.namespaceEndpoint }
    ]

module storage 'modules/storage.bicep' = {
  name: 'storage'
  scope: rg
  params: {
    resourcePrefix: prefix
    location: location
    tags: tags
  }
}

// ---- Compute: Container Apps ----

var commonEnvVars = [
  { name: 'AppConfig__Endpoint', value: appConfig.outputs.endpoint }
  { name: 'KeyVault__Uri', value: keyVault.outputs.uri }
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.outputs.connectionString }
  { name: 'Database__Provider', value: databaseProvider }
]

// Resolved from whichever database module deployed for the selected provider. SQL Hyperscale routes read
// traffic to a secondary replica via ApplicationIntent=ReadOnly on the same server; Postgres uses a distinct
// replica server/FQDN (see the postgres module's readConnectionString output).
var dbConnectionString = databaseProvider == 'SqlServer'
  ? (sqlDatabase.?outputs.?connectionString ?? '')
  : (postgres.?outputs.?connectionString ?? '')
var dbReadConnectionString = databaseProvider == 'SqlServer'
  ? '${sqlDatabase.?outputs.?connectionString ?? ''}ApplicationIntent=ReadOnly;'
  : (postgres.?outputs.?readConnectionString ?? '')

// Auth gap: these apps use Entra auth connection strings, but this template does not yet create
// database users/roles or grants for either provider. Add a data-plane step before production: migrator
// identity gets schema DDL plus migration history rights; API, Scheduler, and Functions get
// runtime DML only on taskflow, flowengine, and Scheduler schemas. Do not grant DDL to runtime apps.
module migrator 'modules/container-app-job.bicep' = {
  name: 'migrator'
  scope: rg
  params: {
    jobName: '${prefix}-dbmigrator'
    location: location
    environmentId: containerAppsEnv.outputs.id
    containerImage: migratorImage
    cpu: '0.25'
    memory: '0.5Gi'
    envVars: union(commonEnvVars, [
      { name: 'ConnectionStrings__TaskFlowDbContextTrxn', value: dbConnectionString }
      { name: 'ConnectionStrings__TaskFlowFlowEngineDbContext', value: dbConnectionString }
      { name: 'ConnectionStrings__TickerQDbContext', value: dbConnectionString }
    ])
    tags: tags
  }
}

module gateway 'modules/container-app.bicep' = {
  name: 'gateway'
  scope: rg
  params: {
    appName: '${prefix}-gateway'
    location: location
    environmentId: containerAppsEnv.outputs.id
    containerImage: gatewayImage
    cpu: gatewayProfile.cpu
    memory: gatewayProfile.memory
    externalIngress: true // Only public endpoint
    targetPort: 8080
    minReplicas: gatewayProfile.minReplicas
    maxReplicas: gatewayProfile.maxReplicas
    concurrentRequests: gatewayProfile.concurrentRequests
    envVars: union(commonEnvVars, [
      { name: 'ReverseProxy__Clusters__api__Destinations__default__Address', value: 'https://${api.outputs.fqdn}' }
      { name: 'AggregateHealthCheck__TaskFlowApiHealthUrl', value: 'https://${api.outputs.fqdn}/health/full' }
      { name: 'AggregateHealthCheck__TaskFlowApiClusterId', value: '' }
    ])
    tags: tags
  }
}

module api 'modules/container-app.bicep' = {
  name: 'api'
  scope: rg
  params: {
    appName: '${prefix}-api'
    location: location
    environmentId: containerAppsEnv.outputs.id
    containerImage: apiImage
    cpu: apiProfile.cpu
    memory: apiProfile.memory
    externalIngress: false // Internal only
    targetPort: 8080
    // D-054: the cleartext HTTP/2 gRPC read listener. external:false keeps it inside the environment -
    // only Blazor calls it, and it carries no auth of its own beyond what the Api's own pipeline applies.
    additionalPortMappings: [
      { external: false, targetPort: apiGrpcPort, exposedPort: apiGrpcPort }
    ]
    minReplicas: apiProfile.minReplicas
    maxReplicas: apiProfile.maxReplicas
    concurrentRequests: apiProfile.concurrentRequests
    envVars: union(commonEnvVars, [
      { name: 'ConnectionStrings__TaskFlowDbContextTrxn', value: dbConnectionString }
      { name: 'ConnectionStrings__TaskFlowDbContextQuery', value: dbReadConnectionString }
      { name: 'ConnectionStrings__TaskFlowFlowEngineDbContext', value: dbConnectionString }
      { name: 'ConnectionStrings__CosmosDb1', value: cosmosDb.outputs.accountEndpoint }
      { name: 'ConnectionStrings__BlobStorage1', value: storage.outputs.appStorageBlobEndpoint }
      { name: 'ConnectionStrings__TableStorage1', value: storage.outputs.appStorageTableEndpoint }
      { name: 'ConnectionStrings__Redis1', value: redis.outputs.connectionString }
    ], messagingEnvVars)
    tags: tags
  }
}

module scheduler 'modules/container-app.bicep' = {
  name: 'scheduler'
  scope: rg
  params: {
    appName: '${prefix}-scheduler'
    location: location
    environmentId: containerAppsEnv.outputs.id
    containerImage: schedulerImage
    cpu: schedulerProfile.cpu
    memory: schedulerProfile.memory
    ingressEnabled: false // No ingress needed
    targetPort: 8080
    minReplicas: schedulerProfile.minReplicas
    maxReplicas: schedulerProfile.maxReplicas
    envVars: union(commonEnvVars, [
      { name: 'ConnectionStrings__TaskFlowDbContextTrxn', value: dbConnectionString }
      { name: 'ConnectionStrings__TaskFlowDbContextQuery', value: dbReadConnectionString }
      { name: 'ConnectionStrings__TaskFlowFlowEngineDbContext', value: dbConnectionString }
      { name: 'ConnectionStrings__TickerQDbContext', value: dbConnectionString }
      { name: 'ConnectionStrings__Redis1', value: redis.outputs.connectionString }
    ], messagingEnvVars)
    tags: tags
  }
}

module blazor 'modules/container-app.bicep' = {
  name: 'blazor'
  scope: rg
  params: {
    appName: '${prefix}-blazor'
    location: location
    environmentId: containerAppsEnv.outputs.id
    containerImage: blazorImage
    cpu: blazorProfile.cpu
    memory: blazorProfile.memory
    externalIngress: true
    targetPort: 8080
    minReplicas: blazorProfile.minReplicas
    maxReplicas: blazorProfile.maxReplicas
    concurrentRequests: blazorProfile.concurrentRequests
    // D-049: the only host that needs affinity. A Blazor Server circuit is per-connection server state, so a
    // reconnect landing on another replica loses it; every other app here is stateless across replicas.
    stickySessions: 'sticky'
    envVars: [
      { name: 'ApiBaseUrl', value: 'https://${gateway.outputs.fqdn}' }
      // D-054: the internal gRPC read hop. Plain http on the additional port mapping, not the https
      // ingress FQDN: the listener is cleartext HTTP/2 inside the environment, and the Api app is
      // external:false so the address is only reachable from within it.
      { name: 'Grpc__TaskFlowRead__Address', value: 'http://${api.outputs.fqdn}:${apiGrpcPort}' }
      { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
      { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.outputs.connectionString }
    ]
    tags: tags
  }
}

module staticWebApp 'modules/static-web-app.bicep' = {
  name: 'staticWebApp'
  scope: rg
  params: {
    appName: '${prefix}-uno'
    location: location
    tags: tags
  }
}

// ---- Compute: Functions ----

module functions 'modules/functions.bicep' = {
  name: 'functions'
  scope: rg
  params: {
    resourcePrefix: prefix
    location: location
    funcStorageAccountName: storage.outputs.funcStorageName
    serviceBusNamespace: serviceBus!.outputs.namespaceEndpoint
    appConfigEndpoint: appConfig.outputs.endpoint
    keyVaultUri: keyVault.outputs.uri
    databaseProvider: databaseProvider
    dbConnectionString: dbConnectionString
    dbReadConnectionString: dbReadConnectionString
    cosmosEndpoint: cosmosDb.outputs.accountEndpoint
    storageBlobEndpoint: storage.outputs.appStorageBlobEndpoint
    appInsightsConnectionString: appInsights.outputs.connectionString
    functionAppScaleLimit: functionAppScaleLimit
    tags: tags
  }
}

// ---- Deploy Identity ----

module deployIdentity 'modules/deploy-identity.bicep' = {
  name: 'deployIdentity'
  scope: rg
  params: {
    identityName: '${prefix}-deploy-id'
    location: location
    tags: tags
  }
}

// ---- RBAC: Deploy Identity -> Resource Group ----

module deployContributor 'modules/role-assignment.bicep' = {
  name: 'deployContributor'
  scope: rg
  params: {
    principalId: deployIdentity.outputs.principalId
    roleDefinitionId: roles.contributor
    roleDescription: 'Deploy identity: Contributor on RG'
  }
}

module deployUaa 'modules/role-assignment.bicep' = {
  name: 'deployUaa'
  scope: rg
  params: {
    principalId: deployIdentity.outputs.principalId
    roleDefinitionId: roles.userAccessAdministrator
    roleDescription: 'Deploy identity: User Access Administrator on RG'
  }
}

// ---- RBAC: Gateway ----

module gatewayAppConfigReader 'modules/role-assignment.bicep' = {
  name: 'gatewayAppConfigReader'
  scope: rg
  params: {
    principalId: gateway.outputs.principalId
    roleDefinitionId: roles.appConfigDataReader
    roleDescription: 'Gateway: App Configuration Data Reader'
  }
}

module gatewayKvSecretsUser 'modules/role-assignment.bicep' = {
  name: 'gatewayKvSecretsUser'
  scope: rg
  params: {
    principalId: gateway.outputs.principalId
    roleDefinitionId: roles.keyVaultSecretsUser
    roleDescription: 'Gateway: Key Vault Secrets User'
  }
}

// ---- RBAC: API ----

module apiAppConfigReader 'modules/role-assignment.bicep' = {
  name: 'apiAppConfigReader'
  scope: rg
  params: {
    principalId: api.outputs.principalId
    roleDefinitionId: roles.appConfigDataReader
    roleDescription: 'API: App Configuration Data Reader'
  }
}

module apiKvSecretsUser 'modules/role-assignment.bicep' = {
  name: 'apiKvSecretsUser'
  scope: rg
  params: {
    principalId: api.outputs.principalId
    roleDefinitionId: roles.keyVaultSecretsUser
    roleDescription: 'API: Key Vault Secrets User'
  }
}

module apiServiceBusSender 'modules/role-assignment.bicep' = if (messagingProvider == 'ServiceBus') {
  name: 'apiServiceBusSender'
  scope: rg
  params: {
    principalId: api.outputs.principalId
    roleDefinitionId: roles.serviceBusDataSender
    roleDescription: 'API: Service Bus Data Sender'
  }
}

module apiBlobContributor 'modules/role-assignment.bicep' = {
  name: 'apiBlobContributor'
  scope: rg
  params: {
    principalId: api.outputs.principalId
    roleDefinitionId: roles.storageBlobDataContributor
    roleDescription: 'API: Storage Blob Data Contributor'
  }
}

module apiTableContributor 'modules/role-assignment.bicep' = {
  name: 'apiTableContributor'
  scope: rg
  params: {
    principalId: api.outputs.principalId
    roleDefinitionId: roles.storageTableDataContributor
    roleDescription: 'API: Storage Table Data Contributor'
  }
}

// ---- RBAC: Scheduler ----

module schedulerAppConfigReader 'modules/role-assignment.bicep' = {
  name: 'schedulerAppConfigReader'
  scope: rg
  params: {
    principalId: scheduler.outputs.principalId
    roleDefinitionId: roles.appConfigDataReader
    roleDescription: 'Scheduler: App Configuration Data Reader'
  }
}

module schedulerKvSecretsUser 'modules/role-assignment.bicep' = {
  name: 'schedulerKvSecretsUser'
  scope: rg
  params: {
    principalId: scheduler.outputs.principalId
    roleDefinitionId: roles.keyVaultSecretsUser
    roleDescription: 'Scheduler: Key Vault Secrets User'
  }
}

module schedulerServiceBusSender 'modules/role-assignment.bicep' = if (messagingProvider == 'ServiceBus') {
  name: 'schedulerServiceBusSender'
  scope: rg
  params: {
    principalId: scheduler.outputs.principalId
    roleDefinitionId: roles.serviceBusDataSender
    roleDescription: 'Scheduler: Service Bus Data Sender'
  }
}

// ---- RBAC: Functions ----

module funcServiceBusReceiver 'modules/role-assignment.bicep' = if (messagingProvider == 'ServiceBus') {
  name: 'funcServiceBusReceiver'
  scope: rg
  params: {
    principalId: functions.outputs.functionAppPrincipalId
    roleDefinitionId: roles.serviceBusDataReceiver
    roleDescription: 'Functions: Service Bus Data Receiver'
  }
}

module funcBlobContributor 'modules/role-assignment.bicep' = {
  name: 'funcBlobContributor'
  scope: rg
  params: {
    principalId: functions.outputs.functionAppPrincipalId
    roleDefinitionId: roles.storageBlobDataContributor
    roleDescription: 'Functions: Storage Blob Data Contributor'
  }
}

module funcAppConfigReader 'modules/role-assignment.bicep' = {
  name: 'funcAppConfigReader'
  scope: rg
  params: {
    principalId: functions.outputs.functionAppPrincipalId
    roleDefinitionId: roles.appConfigDataReader
    roleDescription: 'Functions: App Configuration Data Reader'
  }
}

module funcKvSecretsUser 'modules/role-assignment.bicep' = {
  name: 'funcKvSecretsUser'
  scope: rg
  params: {
    principalId: functions.outputs.functionAppPrincipalId
    roleDefinitionId: roles.keyVaultSecretsUser
    roleDescription: 'Functions: Key Vault Secrets User'
  }
}

// ---- Cosmos DB RBAC (data plane - separate module for RG scope) ----

module cosmosRbac 'modules/cosmos-rbac.bicep' = {
  name: 'cosmosRbac'
  scope: rg
  params: {
    cosmosAccountName: cosmosDb.outputs.accountName
    principalIds: [
      api.outputs.principalId
      functions.outputs.functionAppPrincipalId
    ]
  }
}

// ---- Redis RBAC (data plane - separate module, avoids a circular dependency with api/scheduler) ----

module redisRbac 'modules/redis-rbac.bicep' = {
  name: 'redisRbac'
  scope: rg
  params: {
    redisEnterpriseName: redis.outputs.redisEnterpriseName
    principalIds: [
      api.outputs.principalId
      scheduler.outputs.principalId
    ]
  }
}

// ---- Outputs ----

output resourceGroupName string = rg.name
output gatewayFqdn string = gateway.outputs.fqdn
output apiFqdn string = api.outputs.fqdn
output blazorFqdn string = blazor.outputs.fqdn
output staticWebAppName string = staticWebApp.outputs.name
output staticWebAppDefaultHostname string = staticWebApp.outputs.defaultHostname
output functionAppName string = functions.outputs.functionAppName
output migrationJobName string = migrator.outputs.name
output deployIdentityClientId string = deployIdentity.outputs.clientId
output deployIdentityPrincipalId string = deployIdentity.outputs.principalId
output keyVaultName string = keyVault.outputs.name
output appConfigName string = appConfig.outputs.name
output databaseProviderName string = databaseProvider
output sqlServerName string = databaseProvider == 'SqlServer' ? (sqlDatabase.?outputs.?serverName ?? '') : ''
output postgresServerName string = databaseProvider == 'PostgreSql' ? (postgres.?outputs.?serverName ?? '') : ''
output redisHostName string = redis.outputs.hostName
output appStorageName string = storage.outputs.appStorageName
