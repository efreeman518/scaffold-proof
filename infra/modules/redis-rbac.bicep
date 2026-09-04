@description('Redis Enterprise cluster name (from the redis module output)')
param redisEnterpriseName string

@description('Principal IDs (container app system-assigned identities) granted Entra data-plane access to the default database')
param principalIds array

resource redisEnterprise 'Microsoft.Cache/redisEnterprise@2025-07-01' existing = {
  name: redisEnterpriseName
}

resource redisDatabase 'Microsoft.Cache/redisEnterprise/databases@2025-07-01' existing = {
  parent: redisEnterprise
  name: 'default'
}

// Entra data-plane access grants for the container apps' system-assigned identities. Azure Managed Redis supports
// Entra-based data-plane auth via accessPolicyAssignments, but StackExchange.Redis needs the Microsoft.Azure.StackExchangeRedis
// token-provider package wired in application code to use it (out of scope here - IaC only). Until that lands, the
// redis module's connectionString output authenticates with the primary access key; these grants let a later
// app-code change switch to token auth without an infra change.
resource redisEntraAccess 'Microsoft.Cache/redisEnterprise/databases/accessPolicyAssignments@2025-07-01' = [
  for principalId in principalIds: {
    parent: redisDatabase
    name: replace(principalId, '-', '')
    properties: {
      accessPolicyName: 'default'
      user: {
        objectId: principalId
      }
    }
  }
]
