@description('Container app job name')
param jobName string

@description('Azure region')
param location string

@description('Container Apps Environment ID')
param environmentId string

@description('Container image')
param containerImage string

@description('CPU cores')
param cpu string = '0.25'

@description('Memory')
param memory string = '0.5Gi'

@description('Environment variables')
param envVars array = []

@description('User-assigned managed identity resource ID')
param userAssignedIdentityId string

@description('Replica timeout in seconds')
param replicaTimeout int = 1800

@description('Tags')
param tags object = {}

resource job 'Microsoft.App/jobs@2024-03-01' = {
  name: jobName
  location: location
  tags: tags
  identity: {
    type: 'UserAssigned'
    userAssignedIdentities: {
      '${userAssignedIdentityId}': {}
    }
  }
  properties: {
    environmentId: environmentId
    configuration: {
      replicaRetryLimit: 0
      replicaTimeout: replicaTimeout
      triggerType: 'Manual'
      manualTriggerConfig: {
        replicaCompletionCount: 1
        parallelism: 1
      }
    }
    template: {
      containers: [
        {
          name: jobName
          image: containerImage
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: envVars
        }
      ]
    }
  }
}

output id string = job.id
output name string = job.name
