@description('Resource name prefix')
param resourcePrefix string

@description('Azure region')
param location string

@description('Tags')
param tags object = {}

var uniqueSuffix = uniqueString(resourceGroup().id)

// App data storage (blob + tables)
resource appStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: replace('${resourcePrefix}st${uniqueSuffix}', '-', '')
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false // Entra-only auth
    defaultToOAuthAuthentication: true
    networkAcls: {
      defaultAction: 'Allow' // Dev: allow all; prod: restrict
      bypass: 'AzureServices'
    }
  }
}

resource blobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: appStorage
  name: 'default'
}

resource attachmentsContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServices
  name: 'attachments'
  properties: {
    publicAccess: 'None'
  }
}

// The API persists ASP.NET Core Data Protection keys here. Provisioning it in ARM keeps startup
// read-only with respect to container topology and avoids a data-plane create before Blob RBAC settles.
resource dataProtectionContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: blobServices
  name: 'data-protection'
  properties: {
    publicAccess: 'None'
  }
}

resource tableServices 'Microsoft.Storage/storageAccounts/tableServices@2023-05-01' = {
  parent: appStorage
  name: 'default'
}

resource queueServices 'Microsoft.Storage/storageAccounts/queueServices@2023-05-01' = {
  parent: appStorage
  name: 'default'
}

// Functions runtime storage (requires shared key for Functions runtime)
resource funcStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: replace('${resourcePrefix}fn${uniqueSuffix}', '-', '')
  location: location
  tags: tags
  sku: {
    name: 'Standard_LRS'
  }
  kind: 'StorageV2'
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true // Required for Functions runtime
    networkAcls: {
      defaultAction: 'Allow'
      bypass: 'AzureServices'
    }
  }
}

resource funcBlobServices 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: funcStorage
  name: 'default'
}

// Flex Consumption requires an existing Blob container configured as its deployment storage.
resource functionDeploymentContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: funcBlobServices
  name: 'function-releases'
  properties: {
    publicAccess: 'None'
  }
}

output appStorageName string = appStorage.name
output appStorageBlobEndpoint string = appStorage.properties.primaryEndpoints.blob
output appStorageTableEndpoint string = appStorage.properties.primaryEndpoints.table
output appStorageQueueEndpoint string = appStorage.properties.primaryEndpoints.queue
output funcStorageName string = funcStorage.name
output funcStorageId string = funcStorage.id
output functionDeploymentContainerUri string = '${funcStorage.properties.primaryEndpoints.blob}function-releases'
