@description('Container app name')
param appName string

@description('Azure region')
param location string

@description('Container Apps Environment ID')
param environmentId string

@description('Container image')
param containerImage string = 'mcr.microsoft.com/azuredocs/containerapps-helloworld:latest'

@description('CPU cores')
param cpu string = '0.25'

@description('Memory')
param memory string = '0.5Gi'

@description('Enable external ingress')
param externalIngress bool = false

@description('Enable ingress')
param ingressEnabled bool = true

@description('Target port')
param targetPort int = 8080

@description('Minimum replicas')
param minReplicas int = 0

@description('Maximum replicas')
param maxReplicas int = 1

@description('HTTP concurrent request scale rule threshold. 0 omits the rule (falls back to the platform default).')
param concurrentRequests int = 0

@description('HTTP readiness path (D-049). The revision receives traffic only after this endpoint succeeds.')
param readinessPath string = '/healthz/ready'

@description('HTTP liveness path (D-049). A failure here restarts the container, so it must not probe dependencies.')
param livenessPath string = '/healthz/live'

@description('HTTP startup path (D-049). Holds liveness and readiness off until the app has finished starting.')
param startupPath string = '/healthz/live'

@description('Ingress session affinity. Sticky is required only for hosts holding server-side per-client state (Blazor Server SignalR circuits).')
@allowed(['sticky', 'none'])
param stickySessions string = 'none'

@description('Extra ingress ports beyond targetPort, each { external, targetPort, exposedPort }. D-054 uses one internal TCP mapping for the cleartext HTTP/2 gRPC listener; empty leaves ingress exactly as it was.')
param additionalPortMappings array = []

@description('Environment variables')
param envVars array = []

@description('Tags')
param tags object = {}

resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  name: appName
  location: location
  tags: tags
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    environmentId: environmentId
    configuration: {
      // The additional mappings are folded in with union() rather than written as a literal property so
      // an app that declares none produces byte-identical ingress to before this parameter existed.
      ingress: ingressEnabled ? union(
        {
          external: externalIngress
          targetPort: targetPort
          transport: 'auto'
          allowInsecure: false
          stickySessions: {
            affinity: stickySessions
          }
        },
        empty(additionalPortMappings) ? {} : {
          additionalPortMappings: additionalPortMappings
        }
      ) : null
    }
    template: {
      containers: [
        {
          name: appName
          image: containerImage
          resources: {
            cpu: json(cpu)
            memory: memory
          }
          env: envVars
          // D-049: three probes with distinct jobs. Startup absorbs a slow first start (up to 5 minutes) and
          // suppresses the other two until it passes, so a cold start is never mistaken for a crash loop.
          // Liveness restarts the container and therefore probes only the process. Readiness gates routing
          // and is the one that probes dependencies.
          probes: [
            {
              type: 'Startup'
              httpGet: {
                path: startupPath
                port: targetPort
                scheme: 'HTTP'
              }
              initialDelaySeconds: 5
              periodSeconds: 5
              timeoutSeconds: 3
              failureThreshold: 60
              successThreshold: 1
            }
            {
              type: 'Liveness'
              httpGet: {
                path: livenessPath
                port: targetPort
                scheme: 'HTTP'
              }
              periodSeconds: 30
              timeoutSeconds: 5
              failureThreshold: 3
              successThreshold: 1
            }
            {
              type: 'Readiness'
              httpGet: {
                path: readinessPath
                port: targetPort
                scheme: 'HTTP'
              }
              initialDelaySeconds: 10
              periodSeconds: 10
              timeoutSeconds: 5
              failureThreshold: 18
              successThreshold: 1
            }
          ]
        }
      ]
      scale: union(
        {
          minReplicas: minReplicas
          maxReplicas: maxReplicas
        },
        concurrentRequests > 0 ? {
          rules: [
            {
              name: 'http-concurrency'
              http: {
                metadata: {
                  concurrentRequests: string(concurrentRequests)
                }
              }
            }
          ]
        } : {}
      )
    }
  }
}

output id string = containerApp.id
output name string = containerApp.name
output fqdn string = ingressEnabled && containerApp.properties.configuration.ingress != null ? containerApp.properties.configuration.ingress.fqdn : ''
output principalId string = containerApp.identity.principalId
output latestRevisionName string = containerApp.properties.latestRevisionName
