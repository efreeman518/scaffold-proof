@description('Resource name prefix')
param resourcePrefix string

@description('Azure region')
param location string

@description('Functions storage account name')
param funcStorageAccountName string

@description('Service Bus namespace FQDN')
param serviceBusNamespace string

@description('App Configuration endpoint')
param appConfigEndpoint string

@description('Key Vault URI')
param keyVaultUri string

@description('Search backend (D-040); Azure AI Search is the Azure opt-in and SQL is the local fallback')
@allowed([
  'AzureAiSearch'
  'Sql'
])
param searchProvider string = 'AzureAiSearch'

@description('Primary (read-write) database connection string')
param dbConnectionString string

@description('Read-replica database connection string; falls back to the primary string when no replica exists')
param dbReadConnectionString string

@description('Cosmos DB endpoint')
param cosmosEndpoint string

@description('Storage blob endpoint')
param storageBlobEndpoint string

@description('Storage queue endpoint used by the Blob trigger for poison blobs')
param storageQueueEndpoint string

@description('Storage table endpoint')
param storageTableEndpoint string

@description('Shared Application Insights connection string')
param appInsightsConnectionString string

@description('Maximum function app instance count (Flex Consumption scale-out ceiling); pair with host.json serviceBus.maxConcurrentCalls')
param functionAppScaleLimit int = 20

@description('User-assigned managed identity resource ID used for Azure SQL')
param userAssignedIdentityId string

@description('Tags')
param tags object = {}

var uniqueSuffix = uniqueString(resourceGroup().id)

resource funcStorageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: funcStorageAccountName
}

var funcStorageConnectionString = 'DefaultEndpointsProtocol=https;AccountName=${funcStorageAccount.name};AccountKey=${funcStorageAccount.listKeys().keys[0].value};EndpointSuffix=core.windows.net'

resource flexPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: '${resourcePrefix}-func-plan'
  location: location
  tags: tags
  kind: 'functionapp'
  sku: {
    tier: 'FlexConsumption'
    name: 'FC1'
  }
  properties: {
    reserved: true // Linux
  }
}

resource functionApp 'Microsoft.Web/sites@2024-04-01' = {
  name: '${resourcePrefix}-func-${uniqueSuffix}'
  location: location
  tags: tags
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned, UserAssigned'
    userAssignedIdentities: {
      '${userAssignedIdentityId}': {}
    }
  }
  properties: {
    serverFarmId: flexPlan.id
    httpsOnly: true
    siteConfig: {
      appSettings: [
        { name: 'AzureWebJobsStorage', value: funcStorageConnectionString }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'Hosting__Lane', value: 'Azure' }
        { name: 'Database__Provider', value: 'SqlServer' }
        { name: 'Messaging__Provider', value: 'ServiceBus' }
        { name: 'Storage__Provider', value: 'AzureBlob' }
        { name: 'ReadModel__Provider', value: 'Cosmos' }
        { name: 'Audit__Provider', value: 'AzureTable' }
        { name: 'DataProtection__Persistence', value: 'AzureBlob' }
        { name: 'ServiceBus1__fullyQualifiedNamespace', value: serviceBusNamespace }
        { name: 'DomainEventsTopic', value: 'DomainEvents' }
        { name: 'AppConfig__Endpoint', value: appConfigEndpoint }
        { name: 'KeyVault__Uri', value: keyVaultUri }
        { name: 'Search__Provider', value: searchProvider }
        { name: 'AzureWebJobs.ProcessTaskEmbedding.Disabled', value: 'true' }
        { name: 'ConnectionStrings__TaskFlowDbContextTrxn', value: dbConnectionString }
        { name: 'ConnectionStrings__TaskFlowDbContextQuery', value: dbReadConnectionString }
        { name: 'ConnectionStrings__TaskFlowFlowEngineDbContext', value: dbConnectionString }
        { name: 'ConnectionStrings__CosmosDb1', value: cosmosEndpoint }
        // Azure Functions identity-based Blob bindings require the connection prefix plus blobServiceUri.
        { name: 'BlobStorage1__blobServiceUri', value: storageBlobEndpoint }
        { name: 'BlobStorage1__queueServiceUri', value: storageQueueEndpoint }
        { name: 'ConnectionStrings__TableStorage1', value: storageTableEndpoint }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsightsConnectionString
        }
        // The Functions host process emits request telemetry itself; suppress the worker's ASP.NET Core
        // instrumentation so requests are not double-reported by the Azure Monitor distro in ServiceDefaults.
        { name: 'TASKFLOW_SUPPRESS_ASPNETCORE_INSTRUMENTATION', value: 'true' }
      ]
      minTlsVersion: '1.2'
      ftpsState: 'Disabled'
      functionAppScaleLimit: functionAppScaleLimit
    }
  }
}

output functionAppName string = functionApp.name
output functionAppPrincipalId string = functionApp.identity.principalId
output functionAppHostName string = functionApp.properties.defaultHostName
