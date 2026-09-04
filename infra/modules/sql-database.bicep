@description('Resource name prefix')
param resourcePrefix string

@description('Azure region')
param location string

@description('Entra admin principal ID')
param sqlAdminPrincipalId string

@description('Entra admin principal name')
param sqlAdminPrincipalName string

@description('Entra admin principal type')
@allowed(['User', 'Group', 'Application'])
param sqlAdminPrincipalType string = 'User'

@description('Database SKU name, e.g. Basic (dev) or HS_Gen5_2 (prod Hyperscale)')
param skuName string = 'Basic'

@description('Database SKU tier, e.g. Basic (dev) or Hyperscale (prod)')
param skuTier string = 'Basic'

@description('Database SKU family, required for Hyperscale/vCore tiers (e.g. Gen5). Empty for DTU tiers such as Basic.')
param skuFamily string = ''

@description('Database SKU capacity (vCores for Hyperscale, DTUs for Basic). 0 leaves the ARM default for the tier.')
param skuCapacity int = 0

@description('Zone-redundant deployment (prod Hyperscale)')
param zoneRedundant bool = false

@description('Hyperscale high-availability secondary replica count (prod). 0 disables HA replicas.')
param highAvailabilityReplicaCount int = 0

@description('Hyperscale read-scale routing via ApplicationIntent=ReadOnly (prod)')
param readScaleEnabled bool = false

@description('Maximum SqlClient connection pool size, emitted explicitly in every connection string')
param maxPoolSize int = 100

@description('Tags')
param tags object = {}

var uniqueSuffix = uniqueString(resourceGroup().id)

var sqlSku = union(
  { name: skuName, tier: skuTier },
  empty(skuFamily) ? {} : { family: skuFamily },
  skuCapacity > 0 ? { capacity: skuCapacity } : {}
)

// maxSizeBytes only applies to the Basic DTU tier; Hyperscale grows storage dynamically.
var sqlDatabaseProperties = union(
  { collation: 'SQL_Latin1_General_CP1_CI_AS' },
  skuTier == 'Basic' ? { maxSizeBytes: 2147483648 } : {},
  zoneRedundant ? { zoneRedundant: true } : {},
  highAvailabilityReplicaCount > 0 ? { highAvailabilityReplicaCount: highAvailabilityReplicaCount } : {},
  readScaleEnabled ? { readScale: 'Enabled' } : {}
)

resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = {
  name: '${resourcePrefix}-sql-${uniqueSuffix}'
  location: location
  tags: tags
  properties: {
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: sqlAdminPrincipalType
      login: sqlAdminPrincipalName
      sid: sqlAdminPrincipalId
      tenantId: subscription().tenantId
      azureADOnlyAuthentication: true
    }
    minimalTlsVersion: '1.2'
  }
}

resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = {
  parent: sqlServer
  name: 'taskflowdb'
  location: location
  tags: tags
  sku: sqlSku
  properties: sqlDatabaseProperties
}

// Allow Azure services to access SQL
resource sqlFirewallAzure 'Microsoft.Sql/servers/firewallRules@2023-08-01-preview' = {
  parent: sqlServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

output serverName string = sqlServer.name
output serverFqdn string = sqlServer.properties.fullyQualifiedDomainName
output databaseName string = sqlDatabase.name
// Auth gap: Entra-only connection string does not create contained database users or grants.
// Provision SQL data-plane users separately for each managed identity.
output connectionString string = 'Server=tcp:${sqlServer.properties.fullyQualifiedDomainName},1433;Database=${sqlDatabase.name};Authentication=Active Directory Default;Encrypt=True;TrustServerCertificate=False;Max Pool Size=${maxPoolSize};'
