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

@description('Opt-in for the SQL Always Encrypted demo (D-019): grants Key Vault Crypto User to the API and migrator identities and wires the CMK key URL. Off by default to keep the baseline deploy unchanged.')
param enableAlwaysEncrypted bool = false

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
  keyVaultCryptoUser: '14b46e9e-c2b7-41b4-b07b-48a6ebf60603' // Always Encrypted CMK sign/wrap/unwrap (D-019)
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

module sqlDatabase 'modules/sql-database.bicep' = {
  name: 'sqlDatabase'
  scope: rg
  params: {
    resourcePrefix: prefix
    location: location
    sqlAdminPrincipalId: sqlAdminPrincipalId
    sqlAdminPrincipalName: sqlAdminPrincipalName
    sqlAdminPrincipalType: sqlAdminPrincipalType
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

module serviceBus 'modules/service-bus.bicep' = {
  name: 'serviceBus'
  scope: rg
  params: {
    resourcePrefix: prefix
    location: location
    tags: tags
  }
}

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
]

// Always Encrypted (D-019) env wiring. Migrator creates the CMK/CEK and alters columns (runs setup);
// API registers the AKV provider and turns on column encryption. Both need the CMK key URL.
var alwaysEncryptedMigratorEnvVars = enableAlwaysEncrypted ? [
  { name: 'SKIP_ALWAYS_ENCRYPTED_SETUP', value: 'false' }
  { name: 'AKVCMKURL', value: keyVault.outputs.cmkKeyUri }
] : []
var alwaysEncryptedApiEnvVars = enableAlwaysEncrypted ? [
  { name: 'TASKFLOW_ENABLE_ALWAYS_ENCRYPTED', value: 'true' }
  { name: 'AKVCMKURL', value: keyVault.outputs.cmkKeyUri }
] : []

// SQL auth gap: these apps use Entra auth connection strings, but this template does not yet
// create database users or grants. Add a SQL data-plane step before production: migrator
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
      { name: 'ConnectionStrings__TaskFlowDbContextTrxn', value: sqlDatabase.outputs.connectionString }
      { name: 'ConnectionStrings__TaskFlowFlowEngineDbContext', value: sqlDatabase.outputs.connectionString }
      { name: 'ConnectionStrings__TickerQDbContext', value: sqlDatabase.outputs.connectionString }
    ], alwaysEncryptedMigratorEnvVars)
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
    cpu: '0.25'
    memory: '0.5Gi'
    externalIngress: true // Only public endpoint
    targetPort: 8080
    minReplicas: 0
    maxReplicas: 2
    readinessPath: '/healthz'
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
    cpu: '0.5'
    memory: '1Gi'
    externalIngress: false // Internal only
    targetPort: 8080
    minReplicas: 0
    maxReplicas: 3
    readinessPath: '/health/db'
    envVars: union(commonEnvVars, [
      { name: 'ConnectionStrings__TaskFlowDbContextTrxn', value: sqlDatabase.outputs.connectionString }
      { name: 'ConnectionStrings__TaskFlowDbContextQuery', value: sqlDatabase.outputs.connectionString }
      { name: 'ConnectionStrings__TaskFlowFlowEngineDbContext', value: sqlDatabase.outputs.connectionString }
      { name: 'ConnectionStrings__CosmosDb1', value: cosmosDb.outputs.accountEndpoint }
      { name: 'ConnectionStrings__BlobStorage1', value: storage.outputs.appStorageBlobEndpoint }
      { name: 'ConnectionStrings__TableStorage1', value: storage.outputs.appStorageTableEndpoint }
      { name: 'SERVICEBUS__fullyQualifiedNamespace', value: serviceBus.outputs.namespaceEndpoint }
    ], alwaysEncryptedApiEnvVars)
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
    cpu: '0.25'
    memory: '0.5Gi'
    ingressEnabled: false // No ingress needed
    targetPort: 8080
    minReplicas: 0
    maxReplicas: 1
    envVars: union(commonEnvVars, [
      { name: 'ConnectionStrings__TaskFlowDbContextTrxn', value: sqlDatabase.outputs.connectionString }
      { name: 'ConnectionStrings__TaskFlowDbContextQuery', value: sqlDatabase.outputs.connectionString }
      { name: 'ConnectionStrings__TaskFlowFlowEngineDbContext', value: sqlDatabase.outputs.connectionString }
      { name: 'ConnectionStrings__TickerQDbContext', value: sqlDatabase.outputs.connectionString }
      { name: 'SERVICEBUS__fullyQualifiedNamespace', value: serviceBus.outputs.namespaceEndpoint }
    ])
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
    cpu: '0.25'
    memory: '0.5Gi'
    externalIngress: true
    targetPort: 8080
    minReplicas: 0
    maxReplicas: 1
    envVars: [
      { name: 'ApiBaseUrl', value: 'https://${gateway.outputs.fqdn}' }
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
    serviceBusNamespace: serviceBus.outputs.namespaceEndpoint
    appConfigEndpoint: appConfig.outputs.endpoint
    keyVaultUri: keyVault.outputs.uri
    sqlConnectionString: sqlDatabase.outputs.connectionString
    cosmosEndpoint: cosmosDb.outputs.accountEndpoint
    storageBlobEndpoint: storage.outputs.appStorageBlobEndpoint
    appInsightsConnectionString: appInsights.outputs.connectionString
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

module apiServiceBusSender 'modules/role-assignment.bicep' = {
  name: 'apiServiceBusSender'
  scope: rg
  params: {
    principalId: api.outputs.principalId
    roleDefinitionId: roles.serviceBusDataSender
    roleDescription: 'API: Service Bus Data Sender'
  }
}

// Always Encrypted (D-019): API decrypts (unwrapKey) at runtime; migrator creates the CMK/CEK (sign + wrapKey).
module apiKvCryptoUser 'modules/role-assignment.bicep' = if (enableAlwaysEncrypted) {
  name: 'apiKvCryptoUser'
  scope: rg
  params: {
    principalId: api.outputs.principalId
    roleDefinitionId: roles.keyVaultCryptoUser
    roleDescription: 'API: Key Vault Crypto User (Always Encrypted)'
  }
}

module migratorKvCryptoUser 'modules/role-assignment.bicep' = if (enableAlwaysEncrypted) {
  name: 'migratorKvCryptoUser'
  scope: rg
  params: {
    principalId: migrator.outputs.principalId
    roleDefinitionId: roles.keyVaultCryptoUser
    roleDescription: 'Migrator: Key Vault Crypto User (Always Encrypted)'
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

module schedulerServiceBusSender 'modules/role-assignment.bicep' = {
  name: 'schedulerServiceBusSender'
  scope: rg
  params: {
    principalId: scheduler.outputs.principalId
    roleDefinitionId: roles.serviceBusDataSender
    roleDescription: 'Scheduler: Service Bus Data Sender'
  }
}

// ---- RBAC: Functions ----

module funcServiceBusReceiver 'modules/role-assignment.bicep' = {
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
output sqlServerName string = sqlDatabase.outputs.serverName
output appStorageName string = storage.outputs.appStorageName
