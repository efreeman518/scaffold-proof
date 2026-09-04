@description('Resource name prefix')
param resourcePrefix string

@description('Azure region')
param location string

@description('Entra admin principal ID (object ID)')
param pgAdminPrincipalId string

@description('Entra admin principal name')
param pgAdminPrincipalName string

@description('Entra admin principal type')
@allowed(['User', 'Group', 'ServicePrincipal'])
param pgAdminPrincipalType string = 'User'

@description('PostgreSQL major version')
param postgresVersion string = '17'

@description('Compute SKU name, e.g. Standard_B1ms (dev) or Standard_D2s_v3 (prod)')
param skuName string = 'Standard_B1ms'

@description('Compute SKU tier')
@allowed(['Burstable', 'GeneralPurpose', 'MemoryOptimized'])
param skuTier string = 'Burstable'

@description('Storage size in GB')
param storageSizeGB int = 32

@description('High availability mode: ZoneRedundant for prod, Disabled for dev')
@allowed(['Disabled', 'ZoneRedundant'])
param highAvailabilityMode string = 'Disabled'

@description('Deploy a prod-only read replica')
param deployReadReplica bool = false

@description('Maximum Npgsql connection pool size, emitted explicitly in every connection string')
param maxPoolSize int = 100

@description('Name of the Entra principal (managed identity display name) used as the connecting Npgsql role. Data-plane role creation is a separate step; see comment on connectionString output.')
param entraConnectingPrincipalName string = ''

@description('Tags')
param tags object = {}

var uniqueSuffix = uniqueString(resourceGroup().id)

resource pgServer 'Microsoft.DBforPostgreSQL/flexibleServers@2025-08-01' = {
  name: '${resourcePrefix}-pg-${uniqueSuffix}'
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: skuTier
  }
  properties: {
    version: postgresVersion
    storage: {
      storageSizeGB: storageSizeGB
    }
    highAvailability: {
      mode: highAvailabilityMode
    }
    // Entra-only: no password-based administratorLogin/administratorLoginPassword.
    authConfig: {
      activeDirectoryAuth: 'Enabled'
      passwordAuth: 'Disabled'
      tenantId: subscription().tenantId
    }
  }
}

resource pgAdmin 'Microsoft.DBforPostgreSQL/flexibleServers/administrators@2025-08-01' = {
  parent: pgServer
  name: pgAdminPrincipalId
  properties: {
    principalName: pgAdminPrincipalName
    principalType: pgAdminPrincipalType
    tenantId: subscription().tenantId
  }
}

resource pgDatabase 'Microsoft.DBforPostgreSQL/flexibleServers/databases@2025-08-01' = {
  parent: pgServer
  name: 'taskflowdb'
  properties: {
    charset: 'UTF8'
    collation: 'en_US.utf8'
  }
}

// pgvector allowlist. azure.extensions is a comma-separated allowlist; VECTOR is the only extension TaskFlow needs today.
resource pgVectorExtension 'Microsoft.DBforPostgreSQL/flexibleServers/configurations@2025-08-01' = {
  parent: pgServer
  name: 'azure.extensions'
  properties: {
    source: 'user-override'
    value: 'VECTOR'
  }
}

// Allow Azure services to access PostgreSQL
resource pgFirewallAzure 'Microsoft.DBforPostgreSQL/flexibleServers/firewallRules@2025-08-01' = {
  parent: pgServer
  name: 'AllowAzureServices'
  properties: {
    startIpAddress: '0.0.0.0'
    endIpAddress: '0.0.0.0'
  }
}

// Prod-only read replica. Storage/version are inherited from the source server.
resource pgReplica 'Microsoft.DBforPostgreSQL/flexibleServers@2025-08-01' = if (deployReadReplica) {
  name: '${resourcePrefix}-pg-replica-${uniqueSuffix}'
  location: location
  tags: tags
  sku: {
    name: skuName
    tier: skuTier
  }
  properties: {
    createMode: 'Replica'
    sourceServerResourceId: pgServer.id
  }
}

output serverName string = pgServer.name
output serverFqdn string = pgServer.properties.fullyQualifiedDomainName
output databaseName string = pgDatabase.name
// Auth gap: Entra-only connection strings do not create Postgres roles for the connecting managed identities.
// Provision one Postgres role per app identity (matching its system-assigned identity display name) separately;
// entraConnectingPrincipalName here reflects only the caller-supplied primary/reference identity.
output connectionString string = 'Host=${pgServer.properties.fullyQualifiedDomainName};Port=5432;Database=${pgDatabase.name};Username=${entraConnectingPrincipalName};SSL Mode=Require;Trust Server Certificate=False;Maximum Pool Size=${maxPoolSize}'
output readConnectionString string = 'Host=${pgReplica.?properties.?fullyQualifiedDomainName ?? pgServer.properties.fullyQualifiedDomainName};Port=5432;Database=${pgDatabase.name};Username=${entraConnectingPrincipalName};SSL Mode=Require;Trust Server Certificate=False;Maximum Pool Size=${maxPoolSize}'
