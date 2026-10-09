targetScope = 'resourceGroup'

// The Container Apps environment and the API, Background, MarkItDown, and PageIndex apps. It runs after
// infra/main.bicep, which owns the registry, monitoring, data services, and the API and Background
// identities with their role assignments. Names match the resources infra/main.bicep created before
// this template existed, so redeploying adopts them.

@description('Workload name used in resource names and tags. Must match infra/main.bicep.')
@minLength(2)
@maxLength(15)
param workloadName string = 'sharepointagent'

@description('Environment name used in resource names and tags. Must match infra/main.bicep.')
@minLength(2)
@maxLength(5)
param environmentName string

@description('Region for the environment and apps (infra/main.bicep parameter location). An environment cannot move.')
param location string = resourceGroup().location

param tags object = {}

@description('Outputs of the infra/main.bicep deployment (infra-<environment>), each output name mapped to its value.')
param infrastructure object

@description('Where the agent working directory lives when the API runs the agent itself (ChatAgent:Mode Local): this host disk, a dynamic session (needs infra/DynamicSessions), or a sandbox bound per workspace (needs infra/Sandboxes). Foundry mode already runs the agent in its own session sandbox.')
@allowed([
  'Local'
  'DynamicSessions'
  'Sandboxes'
])
param agentWorkspaceMode string = 'Local'

@description('Turn on graph extraction in the worker.')
param enableGraphRagIndexing bool = false

@description('Compute graph retrieval and record its metrics without showing results to users.')
param enableGraphRagShadowRetrieval bool = false

@description('Add verified graph-derived chunks to chat answers. Enable only after shadow retrieval and evaluation.')
param enableGraphRagRetrieval bool = false

@description('Use release images and runtime settings. False provisions hello containers without application secrets.')
param deployApplicationImages bool = false

@description('API image reference. Set to an existing ACR tag or digest and enable deployApplicationImages for runtime settings.')
param apiImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Background worker image reference; defaults to the public hello image.')
param backgroundImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('MarkItDown image reference; defaults to the public hello image.')
param markItDownImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('PageIndex API image reference; defaults to the public hello image.')
param pageIndexImage string = 'mcr.microsoft.com/k8se/quickstart:latest'

@description('Azure OpenAI API version for PageIndex summary calls.')
param pageIndexAzureApiVersion string = '2024-10-21'

@description('Azure OpenAI deployment used for PageIndex summaries; defaults to the chat deployment.')
param pageIndexDeploymentName string = ''

@secure()
param pageIndexServiceApiKey string = ''

@secure()
param pageIndexAzureApiKey string = ''

param sharePointTenantId string = ''

param sharePointClientId string = ''

param sharePointSiteHostname string = ''

param sharePointSitePath string = ''

param sharePointDocumentLibraryName string = ''

param frontendOrigin string = ''

param bootstrapAdminEmail string = ''

@secure()
param sharePointClientSecret string = ''

@secure()
param sharePointClientState string = ''

@secure()
param markItDownApiKey string = ''

var namePrefix = toLower('${workloadName}-${environmentName}')
var uniqueSuffix = uniqueString(namePrefix, subscription().subscriptionId, resourceGroup().id)
var resourceTags = union(tags, {
  workload: workloadName
  environment: environmentName
  managedBy: 'Bicep'
})
var acrPullRole = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
var hosting = infrastructure.hosting
var apiName = '${namePrefix}-api-${take(uniqueSuffix, 6)}'
var workerName = '${namePrefix}-wrk-${take(uniqueSuffix, 6)}'
var markItDownName = '${namePrefix}-md-${take(uniqueSuffix, 6)}'
var pageIndexName = '${namePrefix}-pi-${take(uniqueSuffix, 6)}'
var foundryEndpoint = '${hosting.foundryProjectEndpoint}/agents/sharepoint-agent/endpoint/protocols/invocations?api-version=v1'

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' existing = {
  name: infrastructure.containerRegistryName
}

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' existing = {
  name: infrastructure.logAnalyticsWorkspaceName
}

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: take(toLower('${namePrefix}-cae-${uniqueSuffix}'), 60)
  location: location
  tags: resourceTags
  properties: {
    appLogsConfiguration: {
      destination: 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalyticsWorkspace.properties.customerId
        sharedKey: logAnalyticsWorkspace.listKeys().primarySharedKey
      }
    }
  }
}

resource markItDownIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-markitdown-pull'
  location: location
  tags: resourceTags
}

resource pageIndexIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-pageindex-pull'
  location: location
  tags: resourceTags
}

resource markItDownRegistry 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, markItDownIdentity.id, acrPullRole)
  scope: containerRegistry
  properties: {
    principalId: markItDownIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRole)
  }
}

resource pageIndexRegistry 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, pageIndexIdentity.id, acrPullRole)
  scope: containerRegistry
  properties: {
    principalId: pageIndexIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRole)
  }
}

var markItDownUrl = 'https://${markItDownName}.${containerAppsEnvironment.properties.defaultDomain}'
var pageIndexUrl = 'https://${pageIndexName}.${containerAppsEnvironment.properties.defaultDomain}'
var apiEndpoint = 'https://${apiName}.${containerAppsEnvironment.properties.defaultDomain}'
// The Graph RAG resources always exist; these flags decide whether the apps use them.
var graphRagEnv = [
  { name: 'GraphRag__IndexingEnabled', value: string(enableGraphRagIndexing) }
  { name: 'GraphRag__ShadowRetrieval', value: string(enableGraphRagShadowRetrieval) }
  { name: 'GraphRag__RetrievalEnabled', value: string(enableGraphRagRetrieval) }
  { name: 'GraphRag__Cosmos__UsedManagedIdentity', value: 'true' }
  { name: 'GraphRag__Cosmos__Endpoint', value: infrastructure.graphCosmosEndpoint }
  { name: 'GraphRag__Archive__UsedManagedIdentity', value: 'true' }
  { name: 'GraphRag__Archive__ServiceUri', value: infrastructure.uploadStorageServiceUri }
]
var commonEnv = [
  { name: 'Monitoring__OpenTelemetry__Exporter', value: 'AzureMonitor' }
  { name: 'Monitoring__OpenTelemetry__Environment', value: environmentName }
  { name: 'Monitoring__OpenTelemetry__AzureMonitor__ConnectionString', value: infrastructure.applicationInsightsConnectionString }
  { name: 'ASPNETCORE_ENVIRONMENT', value: 'Production' }
  { name: 'SqlServer__AutoMigrate', value: 'false' }
  { name: 'SharePoint__TenantId', value: sharePointTenantId }
  { name: 'SharePoint__ClientId', value: sharePointClientId }
  { name: 'SharePoint__ClientSecret', secretRef: 'graph-client-secret' }
  { name: 'SharePoint__ClientState', secretRef: 'webhook-client-state' }
  { name: 'SharePoint__SiteHostname', value: sharePointSiteHostname }
  { name: 'SharePoint__SitePath', value: sharePointSitePath }
  { name: 'SharePoint__DocumentLibraryName', value: sharePointDocumentLibraryName }
  { name: 'SharePoint__NotificationUrl', value: '${apiEndpoint}/api/sharepoint/webhook' }
  { name: 'ServiceBus__Enabled', value: 'true' }
  { name: 'ServiceBus__UsedManagedIdentity', value: 'true' }
  { name: 'ServiceBus__FullyQualifiedNamespace', value: infrastructure.serviceBusFullyQualifiedNamespace }
  { name: 'ServiceBus__TopicName', value: infrastructure.serviceBusTopicName }
  { name: 'ServiceBus__SubscriptionName', value: infrastructure.serviceBusSubscriptionName }
  { name: 'AzureSearch__UsedManagedIdentity', value: 'true' }
  { name: 'AzureSearch__Endpoint', value: infrastructure.searchEndpoint }
  { name: 'AzureOpenAI__UsedManagedIdentity', value: 'true' }
  { name: 'AzureOpenAI__Endpoint', value: infrastructure.openAiEndpoint }
  { name: 'AzureOpenAI__EmbeddingDeployment', value: infrastructure.embeddingDeploymentName }
  { name: 'AzureOpenAI__ChatDeployment', value: infrastructure.chatDeploymentName }
  { name: 'AzureOpenAI__TranscriptionDeployment', value: infrastructure.transcriptionDeploymentName }
  { name: 'MarkItDown__Endpoint', value: markItDownUrl }
  { name: 'PageIndex__Endpoint', value: pageIndexUrl }
  { name: 'MarkItDown__ApiKey', secretRef: 'markitdown-api-key' }
  { name: 'DocumentIntelligence__UsedManagedIdentity', value: 'true' }
  { name: 'DocumentIntelligence__Endpoint', value: infrastructure.documentIntelligenceEndpoint }
]
var secrets = [
  { name: 'graph-client-secret', value: sharePointClientSecret }
  { name: 'webhook-client-state', value: sharePointClientState }
  { name: 'markitdown-api-key', value: markItDownApiKey }
]

resource markItDown 'Microsoft.App/containerApps@2025-01-01' = {
  name: markItDownName
  location: location
  tags: resourceTags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${markItDownIdentity.id}': {} } }
  properties: {
    environmentId: containerAppsEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, allowInsecure: false, targetPort: deployApplicationImages ? 8000 : 80, transport: 'http' }
      registries: [{ server: containerRegistry.properties.loginServer, identity: markItDownIdentity.id }]
      secrets: deployApplicationImages ? [{ name: 'markitdown-api-key', value: markItDownApiKey }] : []
    }
    template: {
      containers: [{
        name: 'markitdown'
        image: markItDownImage
        env: deployApplicationImages ? [{ name: 'MARKITDOWN_API_KEY', secretRef: 'markitdown-api-key' }] : []
        resources: { cpu: json('0.5'), memory: '1Gi' }
        probes: [{ type: 'Readiness', httpGet: { path: deployApplicationImages ? '/health' : '/', port: deployApplicationImages ? 8000 : 80 }, periodSeconds: 10 }]
      }]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
  dependsOn: [markItDownRegistry]
}

resource pageIndex 'Microsoft.App/containerApps@2025-01-01' = {
  name: pageIndexName
  location: location
  tags: resourceTags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${pageIndexIdentity.id}': {} } }
  properties: {
    environmentId: containerAppsEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, allowInsecure: false, targetPort: deployApplicationImages ? 8000 : 80, transport: 'http' }
      registries: [{ server: containerRegistry.properties.loginServer, identity: pageIndexIdentity.id }]
      secrets: deployApplicationImages ? [
        { name: 'pageindex-api-key', value: pageIndexServiceApiKey }
        { name: 'pageindex-azure-api-key', value: pageIndexAzureApiKey }
      ] : []
    }
    template: {
      containers: [{
        name: 'pageindex'
        image: pageIndexImage
        env: deployApplicationImages ? [
          { name: 'PAGEINDEX_SERVICE_API_KEY', secretRef: 'pageindex-api-key' }
          { name: 'AZURE_API_KEY', secretRef: 'pageindex-azure-api-key' }
          { name: 'AZURE_API_BASE', value: infrastructure.openAiEndpoint }
          { name: 'AZURE_API_VERSION', value: pageIndexAzureApiVersion }
          { name: 'PAGEINDEX_INDEX_MODEL', value: 'azure/${empty(pageIndexDeploymentName) ? infrastructure.chatDeploymentName : pageIndexDeploymentName}' }
          { name: 'PAGEINDEX_MAX_FILE_BYTES', value: '26214400' }
          { name: 'PAGEINDEX_TIMEOUT_SECONDS', value: '210' }
          { name: 'PAGEINDEX_MAX_CONCURRENCY', value: '2' }
        ] : []
        resources: { cpu: json('1.0'), memory: '2Gi' }
        probes: [{ type: 'Readiness', httpGet: { path: deployApplicationImages ? '/health' : '/', port: deployApplicationImages ? 8000 : 80 }, periodSeconds: 10 }]
      }]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
  dependsOn: [pageIndexRegistry]
}

// The API and Background identities, their registry pull access, and their data-plane roles come from
// infra/main.bicep, so they are in place before the apps start.
resource api 'Microsoft.App/containerApps@2025-01-01' = {
  name: apiName
  location: location
  tags: resourceTags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${hosting.apiIdentityId}': {} } }
  properties: {
    environmentId: containerAppsEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, allowInsecure: false, targetPort: deployApplicationImages ? 8080 : 80, transport: 'http' }
      registries: [{ server: containerRegistry.properties.loginServer, identity: hosting.apiIdentityId }]
      secrets: deployApplicationImages ? secrets : []
    }
    template: {
      containers: [{
        name: 'api'
        image: apiImage
        env: deployApplicationImages ? concat(commonEnv, graphRagEnv, [
          { name: 'AZURE_CLIENT_ID', value: hosting.apiClientId }
          { name: 'SqlServer__ConnectionString', value: 'Server=tcp:${hosting.sqlServerFqdn},1433;Database=${hosting.sqlDatabaseName};Authentication=Active Directory Managed Identity;User Id=${hosting.apiClientId};Encrypt=True;TrustServerCertificate=False;' }
          { name: 'ChatAgent__Mode', value: 'Foundry' }
          { name: 'AgentWorkspace__Mode', value: agentWorkspaceMode }
          { name: 'AgentWorkspace__DynamicSessions__ManagedIdentityClientId', value: hosting.apiClientId }
          { name: 'AgentWorkspace__Snapshots__UsedManagedIdentity', value: 'true' }
          { name: 'AgentWorkspace__Snapshots__ServiceUri', value: infrastructure.uploadStorageServiceUri }
          { name: 'AgentWorkspace__Sandboxes__SubscriptionId', value: subscription().subscriptionId }
          { name: 'AgentWorkspace__Sandboxes__ResourceGroup', value: resourceGroup().name }
          { name: 'AgentWorkspace__Sandboxes__ManagedIdentityClientId', value: hosting.apiClientId }
          { name: 'ChatAgent__Foundry__Endpoint', value: foundryEndpoint }
          { name: 'ChatAgent__Foundry__ManagedIdentityClientId', value: hosting.apiClientId }
          { name: 'Uploads__UsedManagedIdentity', value: 'true' }
          { name: 'Uploads__ServiceUri', value: infrastructure.uploadStorageServiceUri }
          { name: 'Uploads__ContainerName', value: infrastructure.uploadContainerName }
          { name: 'Cors__AllowedOrigins__0', value: frontendOrigin }
          { name: 'AppIdentity__BootstrapAdminEmails__0', value: bootstrapAdminEmail }
          { name: 'ContentSafety__Enabled', value: string(!empty(infrastructure.contentSafetyEndpoint)) }
          { name: 'ContentSafety__Endpoint', value: infrastructure.contentSafetyEndpoint }
          { name: 'ContentSafety__UseManagedIdentity', value: 'true' }
          { name: 'ContentSafety__ManagedIdentityClientId', value: hosting.apiClientId }
        ]) : []
        resources: { cpu: json('2.0'), memory: '4Gi' }
        probes: [{ type: 'Readiness', httpGet: { path: deployApplicationImages ? '/health' : '/', port: deployApplicationImages ? 8080 : 80 }, periodSeconds: 10 }]
      }]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
  dependsOn: [markItDown]
}

resource background 'Microsoft.App/containerApps@2025-01-01' = {
  name: workerName
  location: location
  tags: resourceTags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${hosting.workerIdentityId}': {} } }
  properties: {
    environmentId: containerAppsEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [{ server: containerRegistry.properties.loginServer, identity: hosting.workerIdentityId }]
      secrets: deployApplicationImages ? secrets : []
    }
    template: {
      containers: [{
        name: 'background'
        image: backgroundImage
        env: deployApplicationImages ? concat(commonEnv, graphRagEnv, [
          { name: 'AZURE_CLIENT_ID', value: hosting.workerClientId }
          { name: 'SqlServer__ConnectionString', value: 'Server=tcp:${hosting.sqlServerFqdn},1433;Database=${hosting.sqlDatabaseName};Authentication=Active Directory Managed Identity;User Id=${hosting.workerClientId};Encrypt=True;TrustServerCertificate=False;' }
        ]) : []
        resources: { cpu: json('1.0'), memory: '2Gi' }
      }]
      // A continuous Service Bus receiver and scheduled reconciliation worker.
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
  dependsOn: [markItDown]
}

output containerAppsEnvironmentName string = containerAppsEnvironment.name
output apiContainerAppName string = api.name
output workerContainerAppName string = background.name
output markItDownContainerAppName string = markItDown.name
output pageIndexContainerAppName string = pageIndex.name
output apiUrl string = apiEndpoint
output markItDownEndpoint string = markItDownUrl
output pageIndexEndpoint string = pageIndexUrl
