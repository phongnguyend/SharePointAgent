targetScope = 'resourceGroup'

// Native Winnow serving the Winnow-E4B Q8 decision model in a Container Apps environment of its own, so it can
// run wherever serverless T4 GPUs and quota are available without touching the environment the
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

@description('Region for the WinnowNative environment and app; it must offer serverless T4 GPUs. Changing it deploys a new environment and app with a new endpoint, and leaves the old ones to delete.')
param winnownativeLocation string = resourceGroup().location

param tags object = {}

@description('Name of the existing container registry (infra/main.bicep output containerRegistryName).')
param containerRegistryName string

@description('Name of the existing Log Analytics workspace (infra/main.bicep output logAnalyticsWorkspaceName).')
param logAnalyticsWorkspaceName string

@description('WinnowNative image; defaults to the public hello image until Release - System One - Winnow Native runs.')
param winnownativeImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Bearer token callers must send. Required for the WinnowNative image, because ingress is external.')
@secure()
param winnownativeApiKey string = ''

@description('Zero lets the GPU scale away when idle; the first request then waits for a replica to start and load the model.')
@minValue(0)
param minReplicas int = 0

@minValue(1)
param maxReplicas int = 1

var namePrefix = toLower('${workloadName}-${environmentName}')
// Same suffix as infra/main.bicep, plus the region, because an environment cannot move: a new region
// gets new resources instead of a failed update.
var uniqueSuffix = uniqueString(namePrefix, subscription().subscriptionId, resourceGroup().id)
var regionSuffix = take(uniqueString(uniqueSuffix, toLower(winnownativeLocation)), 6)
var resourceTags = union(tags, {
  workload: workloadName
  environment: environmentName
  managedBy: 'Bicep'
})
var environmentNameForWinnowNative = '${namePrefix}-wn-cae-${regionSuffix}'
var winnownativeName = '${namePrefix}-wn-${regionSuffix}'
var gpuProfileName = 'gpu-t4'
var helloImage = 'mcr.microsoft.com/k8se/quickstart:latest'
var usesWinnowNativeImage = winnownativeImage != helloImage
var healthPath = usesWinnowNativeImage ? '/health' : '/'
var port = usesWinnowNativeImage ? 8091 : 80
var acrPullRole = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: containerRegistryName
}

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: logAnalyticsWorkspaceName
}

// Serverless GPUs need a workload profiles environment; it has no base fee, so this costs nothing
// while the GPU is scaled to zero.
resource winnownativeEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: environmentNameForWinnowNative
  location: winnownativeLocation
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
      { name: gpuProfileName, workloadProfileType: 'Consumption-GPU-NC8as-T4' }
    ]
  }
}

resource winnownativeIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-winnownative-pull-${regionSuffix}'
  location: winnownativeLocation
  tags: resourceTags
}

resource winnownativeRegistry 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, winnownativeIdentity.id, acrPullRole)
  scope: containerRegistry
  properties: {
    principalId: winnownativeIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRole)
  }
}

resource winnownative 'Microsoft.App/containerApps@2025-01-01' = {
  name: winnownativeName
  location: winnownativeLocation
  tags: resourceTags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${winnownativeIdentity.id}': {} } }
  properties: {
    environmentId: winnownativeEnvironment.id
    workloadProfileName: gpuProfileName
    configuration: {
      activeRevisionsMode: 'Single'
      // External, like MarkItDown, so the apps reach it from their own environment and AgentHost from
      // Foundry; WINNOW_API_KEY guards it.
      ingress: { external: true, allowInsecure: false, targetPort: port, transport: 'http' }
      registries: [{ server: containerRegistry.properties.loginServer, identity: winnownativeIdentity.id }]
      secrets: usesWinnowNativeImage ? [{ name: 'winnownative-api-key', value: winnownativeApiKey }] : []
    }
    template: {
      containers: [{
        name: 'winnownative'
        image: winnownativeImage
        env: usesWinnowNativeImage ? [{ name: 'WINNOW_API_KEY', secretRef: 'winnownative-api-key' }] : []
        // A T4 replica is a whole NC8as_T4 slice: 8 vCPU, 56 GiB, one 16 GB GPU.
        resources: { cpu: json('8.0'), memory: '56Gi' }
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
  dependsOn: [winnownativeRegistry]
}

output winnownativeEnvironmentName string = winnownativeEnvironment.name
output winnownativeLocation string = winnownativeLocation
output winnownativeContainerAppName string = winnownative.name
output winnownativeEndpoint string = 'https://${winnownative.properties.configuration.ingress.fqdn}'
