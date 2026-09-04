@description('Resource name prefix')
param resourcePrefix string

@description('Azure region')
param location string

@description('Redis Enterprise SKU name: small Balanced tier for dev, HA tier for prod (e.g. Balanced_B0 dev, Balanced_B5 or higher prod)')
param skuName string = 'Balanced_B0'

@description('Enable zone-redundant high availability (prod)')
param highAvailability bool = false

@description('Tags')
param tags object = {}

var uniqueSuffix = uniqueString(resourceGroup().id)

resource redisEnterprise 'Microsoft.Cache/redisEnterprise@2025-07-01' = {
  name: '${resourcePrefix}-redis-${uniqueSuffix}'
  location: location
  tags: tags
  sku: {
    name: skuName
  }
  properties: {
    minimumTlsVersion: '1.2'
    highAvailability: highAvailability ? 'Enabled' : 'Disabled'
    publicNetworkAccess: 'Enabled'
  }
}

// NoCluster keeps the default database reachable through a single StackExchange.Redis ConnectionMultiplexer
// (FusionCache L2 backplane) without requiring cluster-aware client configuration.
resource redisDatabase 'Microsoft.Cache/redisEnterprise/databases@2025-07-01' = {
  parent: redisEnterprise
  name: 'default'
  properties: {
    clientProtocol: 'Encrypted'
    clusteringPolicy: 'NoCluster'
    port: 10000
  }
}

output redisEnterpriseName string = redisEnterprise.name
output hostName string = redisEnterprise.properties.hostName
output databaseName string = redisDatabase.name
// Access-key auth (see comment above). Never printed: callers must consume this as an env var value only.
@secure()
output connectionString string = '${redisEnterprise.properties.hostName}:10000,password=${redisDatabase.listKeys().primaryKey},ssl=True,abortConnect=False'
