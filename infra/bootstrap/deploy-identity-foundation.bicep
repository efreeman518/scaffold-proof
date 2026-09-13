// Bootstrap-only identity foundation. A human caller with subscription Contributor plus
// User Access Administrator runs this before GitHub Actions can federate as the deploy identity.

targetScope = 'subscription'

@description('Resource name prefix')
param resourcePrefix string = 'taskflow'

@description('Environment name')
param environmentName string = 'dev'

@description('Azure region')
param location string = 'eastus2'

var prefix = '${resourcePrefix}-${environmentName}'
var tags = {
  environment: environmentName
  project: 'taskflow'
  managedBy: 'bicep'
}
var subscriptionDeploymentRoleName = 'TaskFlow Subscription Deployment Operator (${substring(subscription().subscriptionId, 0, 8)})'
var contributorRoleId = 'b24988ac-6180-42a0-ab88-20f7382dd24c'
var userAccessAdministratorRoleId = '18d7d88d-d35e-4fb5-a5c3-7773c20a72d9'

resource rg 'Microsoft.Resources/resourceGroups@2024-03-01' = {
  name: '${prefix}-rg'
  location: location
  tags: tags
}

module deployIdentity '../modules/deploy-identity.bicep' = {
  name: 'deployIdentityFoundation'
  scope: rg
  params: {
    identityName: '${prefix}-deploy-id'
    location: location
    tags: tags
  }
}

// These use the same deterministic assignment IDs as main.bicep, so the application deployment
// later maintains them without creating duplicate grants.
module deployContributor '../modules/role-assignment.bicep' = {
  name: 'deployContributorFoundation'
  scope: rg
  params: {
    principalId: deployIdentity.outputs.principalId
    roleDefinitionId: contributorRoleId
    roleDescription: 'Deploy identity: Contributor on RG'
  }
}

module deployUaa '../modules/role-assignment.bicep' = {
  name: 'deployUaaFoundation'
  scope: rg
  params: {
    principalId: deployIdentity.outputs.principalId
    roleDefinitionId: userAccessAdministratorRoleId
    roleDescription: 'Deploy identity: User Access Administrator on RG'
  }
}

// This role can submit ARM deployments and create/update their resource group. It cannot manage
// application resources or RBAC outside scopes granted separately by main.bicep.
resource subscriptionDeploymentRole 'Microsoft.Authorization/roleDefinitions@2022-04-01' = {
  name: guid(subscription().id, subscriptionDeploymentRoleName)
  properties: {
    roleName: subscriptionDeploymentRoleName
    description: 'Submit TaskFlow subscription deployments and create or update resource groups.'
    type: 'CustomRole'
    permissions: [
      {
        actions: [
          'Microsoft.Resources/deployments/*'
          'Microsoft.Resources/subscriptions/resourceGroups/read'
          'Microsoft.Resources/subscriptions/resourceGroups/write'
        ]
        notActions: []
        dataActions: []
        notDataActions: []
      }
    ]
    assignableScopes: [
      subscription().id
    ]
  }
}

resource subscriptionDeploymentAssignment 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(subscription().id, '${prefix}-deploy-id', subscriptionDeploymentRole.name)
  properties: {
    principalId: deployIdentity.outputs.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionDeploymentRole.id
  }
}

output deployIdentityClientId string = deployIdentity.outputs.clientId
output deployIdentityPrincipalId string = deployIdentity.outputs.principalId
output deployIdentityName string = deployIdentity.outputs.name
output subscriptionDeploymentRoleId string = subscriptionDeploymentRole.id
