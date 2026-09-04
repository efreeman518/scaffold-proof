using 'main.bicep'

param resourcePrefix = 'taskflow'
param environmentName = 'dev'
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

// Database: SQL Basic DTU, no HA, no read replica.
param databaseProvider = 'SqlServer'
param dbMaxPoolSize = 100
param sqlSkuName = 'Basic'
param sqlSkuTier = 'Basic'
param sqlZoneRedundant = false
param sqlHighAvailabilityReplicaCount = 0
param sqlReadScaleEnabled = false

// Redis: small single-node tier, no HA.
param redisSkuName = 'Balanced_B0'
param redisHighAvailability = false

// Functions: default Flex Consumption scale ceiling.
param functionAppScaleLimit = 20

// Container Apps: scale-to-zero, small ceilings, no HTTP concurrency rule.
param gatewayProfile = {
  minReplicas: 0
  maxReplicas: 2
  concurrentRequests: 0
  cpu: '0.25'
  memory: '0.5Gi'
}
param apiProfile = {
  minReplicas: 0
  maxReplicas: 3
  concurrentRequests: 0
  cpu: '0.5'
  memory: '1Gi'
}
param schedulerProfile = {
  minReplicas: 0
  maxReplicas: 1
  concurrentRequests: 0
  cpu: '0.25'
  memory: '0.5Gi'
}
param blazorProfile = {
  minReplicas: 0
  maxReplicas: 1
  concurrentRequests: 0
  cpu: '0.25'
  memory: '0.5Gi'
}
