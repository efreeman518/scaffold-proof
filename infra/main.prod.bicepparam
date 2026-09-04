using 'main.bicep'

param resourcePrefix = 'taskflow'
param environmentName = 'prod'
param location = 'eastus2'

// SQL Entra admin - set these before deployment:
// az ad signed-in-user show --query "{id:id, name:displayName}" -o json
param sqlAdminPrincipalId = '' // Your Entra user/group object ID
param sqlAdminPrincipalName = '' // Your Entra user/group display name
param sqlAdminPrincipalType = 'User'

// Container images - updated by CI/CD workflow
param gatewayImage = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
param apiImage = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
param schedulerImage = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'
param blazorImage = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

// Database: SQL Hyperscale, zone-redundant, one HA secondary doubling as the read-scale replica
// (ApplicationIntent=ReadOnly routes to it). Prod default stays SqlServer; see the commented
// PostgreSql alternative below to switch providers instead.
param databaseProvider = 'SqlServer'
param dbMaxPoolSize = 200
param sqlSkuName = 'HS_Gen5_2'
param sqlSkuTier = 'Hyperscale'
param sqlSkuFamily = 'Gen5'
param sqlSkuCapacity = 2
param sqlZoneRedundant = true
param sqlHighAvailabilityReplicaCount = 1
param sqlReadScaleEnabled = true

// -- PostgreSql alternative (uncomment and set databaseProvider = 'PostgreSql' above to switch provider) --
// param pgSkuName = 'Standard_D2s_v3'
// param pgSkuTier = 'GeneralPurpose'
// param pgStorageSizeGB = 128
// param pgHighAvailabilityMode = 'ZoneRedundant'
// param pgDeployReadReplica = true

// Redis: HA tier.
param redisSkuName = 'Balanced_B5'
param redisHighAvailability = true

// Functions: same Flex Consumption scale ceiling as dev; bump if event volume requires it.
param functionAppScaleLimit = 20

// Container Apps: always-on minimums, larger ceilings, HTTP concurrency scale rules on Gateway/API.
param gatewayProfile = {
  minReplicas: 2
  maxReplicas: 100
  concurrentRequests: 50
  cpu: '0.5'
  memory: '1Gi'
}
param apiProfile = {
  minReplicas: 2
  maxReplicas: 100
  concurrentRequests: 50
  cpu: '1.0'
  memory: '2Gi'
}
param schedulerProfile = {
  minReplicas: 2
  maxReplicas: 2
  concurrentRequests: 0
  cpu: '0.5'
  memory: '1Gi'
}
param blazorProfile = {
  minReplicas: 2
  maxReplicas: 30
  concurrentRequests: 0
  cpu: '0.5'
  memory: '1Gi'
}
