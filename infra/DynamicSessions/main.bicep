targetScope = 'resourceGroup'

// The custom-container session pool that runs SharePointAgent.SandboxHost, in the Container Apps
// environment that infra/main.bicep owns. Each session runs untrusted agent code, so the pool's pull
// identity is used only to fetch the image and is never exposed inside a session. Names match the
// resources infra/main.bicep created before this template existed, so redeploying adopts them.

@description('Workload name used in resource names and tags. Must match infra/main.bicep.')
@minLength(2)
@maxLength(15)
param workloadName string = 'sharepointagent'

@description('Environment name used in resource names and tags. Must match infra/main.bicep.')
@minLength(2)
@maxLength(5)
param environmentName string

@description('Region of the existing Container Apps environment; a session pool must be in the same region as its environment.')
param location string

param tags object = {}

@description('Name of the existing container registry (infra/main.bicep output containerRegistryName).')
param containerRegistryName string

@description('Name of the existing Container Apps environment (infra/main.bicep output containerAppsEnvironmentName).')
param containerAppsEnvironmentName string

@description('Resource ID of the API identity (infra/main.bicep output hosting.apiIdentityId).')
param apiIdentityId string

@description('Principal ID of the API identity (infra/main.bicep output hosting.apiPrincipalId).')
param apiPrincipalId string

@description('Sandbox host image for the session pool; defaults to the public hello image until Release Dynamic Sessions runs.')
param sandboxHostImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Warm sessions kept ready for instant allocation. Ready sessions are billed while they wait.')
@minValue(0)
param readySessions int = 1

@minValue(1)
param maxSessions int = 20

@description('Seconds a session survives after its last request before its files are destroyed.')
@minValue(300)
@maxValue(3600)
param cooldownSeconds int = 1800

@description('Allow session code to reach the internet, for example to pip or npm install packages.')
param egressEnabled bool = true

var namePrefix = toLower('${workloadName}-${environmentName}')
var compactPrefix = replace(namePrefix, '-', '')
var uniqueSuffix = uniqueString(namePrefix, subscription().subscriptionId, resourceGroup().id)
var resourceTags = union(tags, {
  workload: workloadName
  environment: environmentName
  managedBy: 'Bicep'
})
var helloImage = 'mcr.microsoft.com/k8se/quickstart:latest'
var poolName = '${compactPrefix}sessions${take(uniqueSuffix, 6)}'
var port = startsWith(sandboxHostImage, helloImage) ? 80 : 8080
var healthPath = startsWith(sandboxHostImage, helloImage) ? '/' : '/health'
var acrPullRole = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var sessionExecutorRole = '0fb8eba5-a2bb-4abe-b1c1-49dfad359bb0'

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: containerRegistryName
}

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' existing = {
  name: containerAppsEnvironmentName
}

resource poolIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-sessions-pull'
  location: location
  tags: resourceTags
}

resource poolRegistry 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, poolIdentity.id, acrPullRole)
  scope: containerRegistry
  properties: {
    principalId: poolIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRole)
  }
}

resource pool 'Microsoft.App/sessionPools@2026-07-01' = {
  name: poolName
  location: location
  tags: resourceTags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${poolIdentity.id}': {} } }
  properties: {
    environmentId: containerAppsEnvironment.id
    poolManagementType: 'Dynamic'
    containerType: 'CustomContainer'
    customContainerTemplate: {
      containers: [{
        name: 'sandboxhost'
        image: sandboxHostImage
        // The pool authenticates callers with Entra and forwards through ingress with a 240-second limit.
        env: [
          { name: 'Sandbox__RequireApiKey', value: 'false' }
          { name: 'Sandbox__MaxTimeoutSeconds', value: '220' }
        ]
        resources: { cpu: 1, memory: '2Gi' }
        probes: [
          { type: 'Liveness', httpGet: { path: healthPath, port: port }, periodSeconds: 10, failureThreshold: 3 }
          { type: 'Startup', httpGet: { path: healthPath, port: port }, periodSeconds: 5, failureThreshold: 30 }
        ]
      }]
      ingress: { targetPort: port }
      registryCredentials: { server: containerRegistry.properties.loginServer, identity: poolIdentity.id }
    }
    // 'None' keeps the pull identity out of the sessions, where agent code could request its tokens.
    managedIdentitySettings: [{ identity: poolIdentity.id, lifecycle: 'None' }]
    dynamicPoolConfiguration: {
      lifecycleConfiguration: { lifecycleType: 'Timed', cooldownPeriodInSeconds: cooldownSeconds }
    }
    scaleConfiguration: { maxConcurrentSessions: maxSessions, readySessionInstances: readySessions }
    sessionNetworkConfiguration: { status: egressEnabled ? 'EgressEnabled' : 'EgressDisabled' }
  }
  dependsOn: [poolRegistry]
}

resource apiPool 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(pool.id, apiIdentityId, sessionExecutorRole)
  scope: pool
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sessionExecutorRole)
  }
}

// Lets Release Dynamic Sessions smoke-test the pool with the deployment identity.
resource deployerPool 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(pool.id, deployer().objectId, sessionExecutorRole)
  scope: pool
  properties: {
    principalId: deployer().objectId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sessionExecutorRole)
  }
}

output dynamicSessionsPoolName string = pool.name
output dynamicSessionsPoolEndpoint string = pool.properties.poolManagementEndpoint
