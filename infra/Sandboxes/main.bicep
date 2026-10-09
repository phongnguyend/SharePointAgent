targetScope = 'resourceGroup'

// An Azure Container Apps sandbox group whose sandboxes boot SharePointAgent.SandboxHost. Sandboxes and
// their disk images are data-plane objects; this provisions only the group they live in. Each sandbox
// runs untrusted agent code, so the group's pull identity is used only to fetch images. Names match the
// resources infra/main.bicep created before this template existed, so redeploying adopts them.

@description('Workload name used in resource names and tags. Must match infra/main.bicep.')
@minLength(2)
@maxLength(15)
param workloadName string = 'sharepointagent'

@description('Environment name used in resource names and tags. Must match infra/main.bicep.')
@minLength(2)
@maxLength(5)
param environmentName string

@description('Region for the sandbox group; confirm that Sandboxes is available in the subscription and region first. Changing it after the first deployment fails, because the names stay the same.')
param sandboxGroupLocation string = resourceGroup().location

param tags object = {}

@description('Name of the existing container registry (infra/main.bicep output containerRegistryName).')
param containerRegistryName string

@description('Resource ID of the API identity (infra/main.bicep output hosting.apiIdentityId).')
param apiIdentityId string

@description('Principal ID of the API identity (infra/main.bicep output hosting.apiPrincipalId).')
param apiPrincipalId string

var namePrefix = toLower('${workloadName}-${environmentName}')
var uniqueSuffix = uniqueString(namePrefix, subscription().subscriptionId, resourceGroup().id)
var resourceTags = union(tags, {
  workload: workloadName
  environment: environmentName
  managedBy: 'Bicep'
})
var acrPullRole = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var sandboxGroupDataOwnerRole = 'c24cf47c-5077-412d-a19c-45202126392c'

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: containerRegistryName
}

resource sandboxesIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-sandboxes-pull'
  location: sandboxGroupLocation
  tags: resourceTags
}

resource sandboxesRegistry 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, sandboxesIdentity.id, acrPullRole)
  scope: containerRegistry
  properties: {
    principalId: sandboxesIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRole)
  }
}

// Bicep has no type information for this preview API, so property errors appear only at deployment.
resource sandboxGroup 'Microsoft.App/sandboxGroups@2026-02-01-preview' = {
  name: '${namePrefix}-sbx-${take(uniqueSuffix, 6)}'
  location: sandboxGroupLocation
  tags: resourceTags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${sandboxesIdentity.id}': {} } }
  properties: {}
  dependsOn: [sandboxesRegistry]
}

resource apiSandboxGroup 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sandboxGroup.id, apiIdentityId, sandboxGroupDataOwnerRole)
  scope: sandboxGroup
  properties: {
    principalId: apiPrincipalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sandboxGroupDataOwnerRole)
  }
}

// Lets the deployment identity register disk images after Release Sandboxes pushes a new image.
resource deployerSandboxGroup 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(sandboxGroup.id, deployer().objectId, sandboxGroupDataOwnerRole)
  scope: sandboxGroup
  properties: {
    principalId: deployer().objectId
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', sandboxGroupDataOwnerRole)
  }
}

output sandboxGroupName string = sandboxGroup.name
output sandboxGroupId string = sandboxGroup.id
output sandboxGroupLocation string = sandboxGroupLocation
output sandboxesPullIdentityId string = sandboxesIdentity.id
