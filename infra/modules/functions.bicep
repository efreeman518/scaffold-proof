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

@description('Search backend (D-040); PgVector enables the embedding trigger and its subscription')
@allowed([
  'AzureAiSearch'
  'PgVector'
  'Sql'
])
param searchProvider string = 'AzureAiSearch'

@description('Active database provider (SqlServer or PostgreSql)')
@allowed(['SqlServer', 'PostgreSql'])
param databaseProvider string = 'SqlServer'

@description('Primary (read-write) database connection string')
param dbConnectionString string

@description('Read-replica database connection string; falls back to the primary string when no replica exists')
param dbReadConnectionString string

@description('Cosmos DB endpoint')
param cosmosEndpoint string

@description('Storage blob endpoint')
param storageBlobEndpoint string

@description('Shared Application Insights connection string')
param appInsightsConnectionString string

@description('Maximum function app instance count (Flex Consumption scale-out ceiling); pair with host.json serviceBus.maxConcurrentCalls')
param functionAppScaleLimit int = 20

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
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: flexPlan.id
    httpsOnly: true
    siteConfig: {
      appSettings: [
        { name: 'AzureWebJobsStorage', value: funcStorageConnectionString }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'SERVICEBUS__fullyQualifiedNamespace', value: serviceBusNamespace }
        { name: 'AppConfig__Endpoint', value: appConfigEndpoint }
        { name: 'KeyVault__Uri', value: keyVaultUri }
        { name: 'Database__Provider', value: databaseProvider }
        { name: 'Search__Provider', value: searchProvider }
        // D-040: the embedding subscription is created only for PgVector (service-bus.bicep). Off any other
        // arm the trigger is switched off by name rather than removed, the same way D-034 switches off the
        // Service Bus triggers on the RabbitMq lane, so one deployment can flip providers.
        { name: 'AzureWebJobs.ProcessTaskEmbedding.Disabled', value: searchProvider == 'PgVector' ? 'false' : 'true' }
        { name: 'ConnectionStrings__TaskFlowDbContextTrxn', value: dbConnectionString }
        { name: 'ConnectionStrings__TaskFlowDbContextQuery', value: dbReadConnectionString }
        { name: 'ConnectionStrings__TaskFlowFlowEngineDbContext', value: dbConnectionString }
        { name: 'ConnectionStrings__CosmosDb1', value: cosmosEndpoint }
        { name: 'ConnectionStrings__BlobStorage1', value: storageBlobEndpoint }
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
