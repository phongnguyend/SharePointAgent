targetScope = 'resourceGroup'

// Native Laya serving English and multilingual decision models in a Container Apps environment of its own, so it can
// run wherever CPU consumption and quota are available without touching the environment the
// other apps share. It reuses the registry and Log Analytics workspace that infra/main.bicep owns.
// The apps reach it over its public HTTPS endpoint with the API key, from any environment or region.

@description('Workload name used in resource names and tags. Must match infra/main.bicep.')
@minLength(2)
@maxLength(15)
param workloadName string = 'sharepointagent'

@description('Environment name used in resource names and tags. Must match infra/main.bicep.')
@minLength(2)
@maxLength(5)
param environmentName string

@description('Region for the LayaNative environment and app; it must offer CPU consumption. Changing it deploys a new environment and app with a new endpoint, and leaves the old ones to delete.')
param layanativeLocation string = resourceGroup().location

param tags object = {}

@description('Name of the existing container registry (infra/main.bicep output containerRegistryName).')
param containerRegistryName string

@description('Name of the existing Log Analytics workspace (infra/main.bicep output logAnalyticsWorkspaceName).')
param logAnalyticsWorkspaceName string

@description('LayaNative image; defaults to the public hello image until Release LayaNative runs.')
param layanativeImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Bearer token callers must send. Required for the LayaNative image, because ingress is external.')
@secure()
param layanativeApiKey string = ''

@description('Zero lets the CPU scale away when idle; the first request then waits for a replica to start and load the model.')
@minValue(0)
param minReplicas int = 0

@minValue(1)
param maxReplicas int = 1

var namePrefix = toLower('${workloadName}-${environmentName}')
// Same suffix as infra/main.bicep, plus the region, because an environment cannot move: a new region
// gets new resources instead of a failed update.
var uniqueSuffix = uniqueString(namePrefix, subscription().subscriptionId, resourceGroup().id)
var regionSuffix = take(uniqueString(uniqueSuffix, toLower(layanativeLocation)), 6)
var resourceTags = union(tags, {
  workload: workloadName
  environment: environmentName
  managedBy: 'Bicep'
})
var environmentNameForLayaNative = '${namePrefix}-ln-cae-${regionSuffix}'
var layanativeName = '${namePrefix}-ln-${regionSuffix}'
var consumptionProfileName = 'Consumption'
var helloImage = 'mcr.microsoft.com/k8se/quickstart:latest'
var usesLayaNativeImage = layanativeImage != helloImage
var healthPath = usesLayaNativeImage ? '/health' : '/'
var port = usesLayaNativeImage ? 8000 : 80
var acrPullRole = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: containerRegistryName
}

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: logAnalyticsWorkspaceName
}

// A separate consumption environment isolates this service; compute scales away
// while replicas are scaled to zero.
resource layanativeEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: environmentNameForLayaNative
  location: layanativeLocation
  tags: resourceTags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsWorkspace.properties.customerId
        sharedKey: logAnalyticsWorkspace.listKeys().primarySharedKey
      }
    }
    workloadProfiles: [
      { name: 'Consumption', workloadProfileType: 'Consumption' }

    ]
  }
}

resource layanativeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-layanative-pull-${regionSuffix}'
  location: layanativeLocation
  tags: resourceTags
}

resource layanativeRegistry 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, layanativeIdentity.id, acrPullRole)
  scope: containerRegistry
  properties: {
    principalId: layanativeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRole)
  }
}

resource layanative 'Microsoft.App/containerApps@2025-01-01' = {
  name: layanativeName
  location: layanativeLocation
  tags: resourceTags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${layanativeIdentity.id}': {} } }
  properties: {
    environmentId: layanativeEnvironment.id
    workloadProfileName: consumptionProfileName
    configuration: {
      activeRevisionsMode: 'Single'
      // External, like MarkItDown, so the apps reach it from their own environment and AgentHost from
      // Foundry; LAYA_API_KEY guards it.
      ingress: { external: true, allowInsecure: false, targetPort: port, transport: 'http' }
      registries: [{ server: containerRegistry.properties.loginServer, identity: layanativeIdentity.id }]
      secrets: usesLayaNativeImage ? [{ name: 'layanative-api-key', value: layanativeApiKey }] : []
    }
    template: {
      containers: [{
        name: 'layanative'
        image: layanativeImage
        env: usesLayaNativeImage ? [{ name: 'LAYA_API_KEY', secretRef: 'layanative-api-key' }] : []
        // CPU inference with two resident checkpoints.
        resources: { cpu: json('4.0'), memory: '8Gi' }
        // Startup budget covers checkpoint loading; health probes do not require authentication.
        probes: [
          { type: 'Startup', httpGet: { path: healthPath, port: port }, periodSeconds: 60, failureThreshold: 10, timeoutSeconds: 3 }
          { type: 'Liveness', tcpSocket: { port: port }, periodSeconds: 30 }
          { type: 'Readiness', httpGet: { path: healthPath, port: port }, periodSeconds: 10, timeoutSeconds: 3 }
        ]
      }]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [{ name: 'http', http: { metadata: { concurrentRequests: '4' } } }]
      }
    }
  }
  dependsOn: [layanativeRegistry]
}

output layanativeEnvironmentName string = layanativeEnvironment.name
output layanativeLocation string = layanativeLocation
output layanativeContainerAppName string = layanative.name
output layanativeEndpoint string = 'https://${layanative.properties.configuration.ingress.fqdn}'
