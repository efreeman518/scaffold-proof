@description('Resource name prefix')
param resourcePrefix string

@description('Azure region')
param location string

@description('Tags')
param tags object = {}

var uniqueSuffix = uniqueString(resourceGroup().id)

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2024-05-15' = {
  name: '${resourcePrefix}-cosmos-${uniqueSuffix}'
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    capabilities: [
      { name: 'EnableServerless' }
    ]
    consistencyPolicy: {
      defaultConsistencyLevel: 'Session'
    }
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    disableLocalAuth: true // Entra-only auth
    minimalTlsVersion: 'Tls12'
  }
}

// Names must match src/Host/TaskFlow.Api/appsettings.json Cosmos:TaskViews:DatabaseName/ContainerName.
resource cosmosDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-05-15' = {
  parent: cosmosAccount
  name: 'taskflow-db'
  properties: {
    resource: {
      id: 'taskflow-db'
    }
  }
}

resource cosmosContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-05-15' = {
  parent: cosmosDatabase
  name: 'task-views'
  properties: {
    resource: {
      id: 'task-views'
      partitionKey: {
        paths: ['/tenantId']
        kind: 'Hash'
      }
    }
  }
}

output accountName string = cosmosAccount.name
output accountEndpoint string = cosmosAccount.properties.documentEndpoint
output databaseName string = cosmosDatabase.name
