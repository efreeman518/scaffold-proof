@description('Resource name prefix')
param resourcePrefix string

@description('Azure region')
param location string

@description('Container Apps Environment ID')
param environmentId string

@description('Container Apps Environment name, needed to attach the Azure Files storage mount')
param environmentName string

@description('Storage account that hosts the file share backing the broker volume')
param storageAccountName string

@description('RabbitMQ container image, pinned so a broker restart cannot pick up a new major version')
param containerImage string = 'rabbitmq:4.1-management'

@description('Broker user name')
param brokerUser string = 'taskflow'

@description('Broker password')
@secure()
param brokerPassword string

@description('CPU cores')
param cpu string = '1.0'

@description('Memory')
param memory string = '2Gi'

@description('Tags')
param tags object = {}

var fileShareName = 'rabbitmq-data'
var storageMountName = 'rabbitmq-storage'

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-05-01' existing = {
  name: storageAccountName
}

resource fileServices 'Microsoft.Storage/storageAccounts/fileServices@2023-05-01' = {
  parent: storageAccount
  name: 'default'
}

// The broker's mnesia directory. Without a durable volume every restart loses the queues, the topology and any
// message that had not yet been consumed - the outbox would replay them, but the dead-letter queue would not.
resource fileShare 'Microsoft.Storage/storageAccounts/fileServices/shares@2023-05-01' = {
  parent: fileServices
  name: fileShareName
  properties: {
    shareQuota: 100
  }
}

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2024-03-01' existing = {
  name: environmentName
}

resource environmentStorage 'Microsoft.App/managedEnvironments/storages@2024-03-01' = {
  parent: containerAppsEnvironment
  name: storageMountName
  properties: {
    azureFile: {
      accountName: storageAccountName
      accountKey: storageAccount.listKeys().keys[0].value
      shareName: fileShare.name
      accessMode: 'ReadWrite'
    }
  }
}

// Single node: this is the dev/staging proof for the RabbitMQ provider (D-034). Production needs a managed
// broker or a real cluster - see infra/README.md. minReplicas == maxReplicas == 1 is load-bearing: a second
// replica would be a second, independent broker sharing one file share, not a cluster member.
resource rabbitMq 'Microsoft.App/containerApps@2024-03-01' = {
  name: '${resourcePrefix}-rabbitmq'
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    environmentId: environmentId
    configuration: {
      secrets: [
        {
          name: 'broker-password'
          value: brokerPassword
        }
      ]
      ingress: {
        external: false
        targetPort: 5672
        exposedPort: 5672
        transport: 'tcp'
        additionalPortMappings: [
          {
            external: false
            targetPort: 15672
            exposedPort: 15672
          }
        ]
      }
    }
    template: {
      containers: [
        {
          name: 'rabbitmq'
          image: containerImage
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: [
            { name: 'RABBITMQ_DEFAULT_USER', value: brokerUser }
            { name: 'RABBITMQ_DEFAULT_PASS', secretRef: 'broker-password' }
          ]
          volumeMounts: [
            {
              volumeName: storageMountName
              mountPath: '/var/lib/rabbitmq/mnesia'
            }
          ]
          probes: [
            {
              type: 'Readiness'
              tcpSocket: {
                port: 5672
              }
              initialDelaySeconds: 15
              periodSeconds: 10
              timeoutSeconds: 5
              failureThreshold: 12
            }
          ]
        }
      ]
      volumes: [
        {
          name: storageMountName
          storageType: 'AzureFile'
          storageName: environmentStorage.name
        }
      ]
      scale: {
        minReplicas: 1
        maxReplicas: 1
      }
    }
  }
}

output name string = rabbitMq.name
output host string = rabbitMq.properties.configuration.ingress.fqdn
output managementPort int = 15672
