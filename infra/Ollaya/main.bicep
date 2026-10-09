targetScope = 'resourceGroup'

// Ollaya serving the winnow:e4b decision model in a Container Apps environment of its own, so it can
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

@description('Region for the Ollaya environment and app; it must offer serverless T4 GPUs. Changing it deploys a new environment and app with a new endpoint, and leaves the old ones to delete.')
param ollayaLocation string = resourceGroup().location

param tags object = {}

@description('Name of the existing container registry (infra/main.bicep output containerRegistryName).')
param containerRegistryName string

@description('Name of the existing Log Analytics workspace (infra/main.bicep output logAnalyticsWorkspaceName).')
param logAnalyticsWorkspaceName string

@description('Ollaya image; defaults to the public hello image until Release Ollaya runs.')
param ollayaImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Bearer token callers must send. Required for the Ollaya image, because ingress is external.')
@secure()
param ollayaApiKey string = ''

@description('Zero lets the GPU scale away when idle; the first request then waits for a replica to start and load the model.')
@minValue(0)
param minReplicas int = 0

@minValue(1)
param maxReplicas int = 1

var namePrefix = toLower('${workloadName}-${environmentName}')
// Same suffix as infra/main.bicep, plus the region, because an environment cannot move: a new region
// gets new resources instead of a failed update.
var uniqueSuffix = uniqueString(namePrefix, subscription().subscriptionId, resourceGroup().id)
var regionSuffix = take(uniqueString(uniqueSuffix, toLower(ollayaLocation)), 6)
var resourceTags = union(tags, {
  workload: workloadName
  environment: environmentName
  managedBy: 'Bicep'
})
var environmentNameForOllaya = '${namePrefix}-ol-cae-${regionSuffix}'
var ollayaName = '${namePrefix}-ol-${regionSuffix}'
var gpuProfileName = 'gpu-t4'
var helloImage = 'mcr.microsoft.com/k8se/quickstart:latest'
var usesOllayaImage = !startsWith(ollayaImage, helloImage)
var port = usesOllayaImage ? 11435 : 80
var acrPullRole = '7f951dda-4ed3-4680-a7ca-43fe172d538d'

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: containerRegistryName
}

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: logAnalyticsWorkspaceName
}

// Serverless GPUs need a workload profiles environment; it has no base fee, so this costs nothing
// while the GPU is scaled to zero.
resource ollayaEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: environmentNameForOllaya
  location: ollayaLocation
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

resource ollayaIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-ollaya-pull-${regionSuffix}'
  location: ollayaLocation
  tags: resourceTags
}

resource ollayaRegistry 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, ollayaIdentity.id, acrPullRole)
  scope: containerRegistry
  properties: {
    principalId: ollayaIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRole)
  }
}

resource ollaya 'Microsoft.App/containerApps@2025-01-01' = {
  name: ollayaName
  location: ollayaLocation
  tags: resourceTags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${ollayaIdentity.id}': {} } }
  properties: {
    environmentId: ollayaEnvironment.id
    workloadProfileName: gpuProfileName
    configuration: {
      activeRevisionsMode: 'Single'
      // External, like MarkItDown, so the apps reach it from their own environment and AgentHost from
      // Foundry; OLLAYA_API_KEY guards it.
      ingress: { external: true, allowInsecure: false, targetPort: port, transport: 'http' }
      registries: [{ server: containerRegistry.properties.loginServer, identity: ollayaIdentity.id }]
      secrets: usesOllayaImage ? [{ name: 'ollaya-api-key', value: ollayaApiKey }] : []
    }
    template: {
      containers: [{
        name: 'ollaya'
        image: ollayaImage
        env: usesOllayaImage ? [{ name: 'OLLAYA_API_KEY', secretRef: 'ollaya-api-key' }] : []
        // A T4 replica is a whole NC8as_T4 slice: 8 vCPU, 56 GiB, one 16 GB GPU.
        resources: { cpu: json('8.0'), memory: '56Gi' }
        // GET / answers "Ollaya is running" without the API key.
        probes: [
          { type: 'Liveness', httpGet: { path: '/', port: port }, periodSeconds: 30 }
          { type: 'Readiness', httpGet: { path: '/', port: port }, periodSeconds: 10 }
        ]
      }]
      scale: {
        minReplicas: minReplicas
        maxReplicas: maxReplicas
        rules: [{ name: 'http', http: { metadata: { concurrentRequests: '20' } } }]
      }
    }
  }
  dependsOn: [ollayaRegistry]
}

output ollayaEnvironmentName string = ollayaEnvironment.name
output ollayaLocation string = ollayaLocation
output ollayaContainerAppName string = ollaya.name
output ollayaEndpoint string = 'https://${ollaya.properties.configuration.ingress.fqdn}'
