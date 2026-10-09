targetScope = 'resourceGroup'

@description('Workload name used in resource names and tags. Use lowercase letters and digits.')
@minLength(2)
@maxLength(15)
param workloadName string = 'sharepointagent'

@description('Environment name used in resource names and tags, such as local, dev, test, or prod. Use lowercase letters and digits.')
@minLength(2)
@maxLength(5)
param environmentName string

@description('Azure region for resources. Azure OpenAI model availability varies by region.')
param location string = resourceGroup().location

@description('Azure region for the Static Web App; configure a supported region in the environment parameter file.')
param staticWebAppLocation string = location

@allowed([
  'Free'
  'Standard'
])
param staticWebAppSku string = 'Free'

@description('Tags applied to all supported resources.')
param tags object = {}

@description('Object ID of the Entra principal configured as the Azure SQL administrator.')
param sqlEntraAdminObjectId string

@allowed([
  'Application'
  'Group'
  'User'
])
@description('Entra SQL administrator type. Managed identities and service principals use Application.')
param sqlEntraAdministratorPrincipalType string = 'Application'

param sqlEntraAdminName string = 'sharepoint-agent-deployment'
param sqlLocation string = location
param sqlDatabaseName string = 'sharepointagent'
@description('SQL database DTU SKU. Basic is the minimal default; use an S-series SKU for Standard.')
param sqlDatabaseSku string = 'Basic'
param foundryLocation string = location
param chatDeploymentName string = 'gpt-5-mini'
param chatModelName string = 'gpt-5-mini'
param chatModelVersion string = '2025-08-07'
@minValue(1)
param chatDeploymentCapacity int = 10

@description('Deploy a speech-to-text model for chat dictation. Check that the model is available for GlobalStandard in the OpenAI account region first.')
param deployTranscription bool = false
param transcriptionDeploymentName string = 'gpt-4o-transcribe'
param transcriptionModelName string = 'gpt-4o-transcribe'
param transcriptionModelVersion string = '2025-03-20'
@minValue(1)
param transcriptionDeploymentCapacity int = 10

@description('Allow Service Bus shared-access-key connection strings.')
param allowServiceBusLocalAuth bool = true

@description('Allow Azure AI Search API-key authentication.')
param allowSearchApiKeyAuth bool = true

@description('Allow Azure OpenAI API-key authentication.')
param allowOpenAiApiKeyAuth bool = true

@description('Deploy Azure AI Document Intelligence.')
param deployDocumentIntelligence bool = false

@description('Allow Document Intelligence API-key authentication when it is deployed.')
param allowDocumentIntelligenceApiKeyAuth bool = true

@description('Deploy Azure AI Content Safety.')
param deployContentSafety bool = true

@description('Content Safety region. Override if it is unavailable in the shared resource region.')
param contentSafetyLocation string = location

@description('Allow Content Safety API-key authentication for local development.')
param allowContentSafetyApiKeyAuth bool = true

@description('Azure OpenAI embedding deployment name used by the application.')
param embeddingDeploymentName string = 'text-embedding-3-small'

@description('Azure OpenAI embedding model name.')
param embeddingModelName string = 'text-embedding-3-small'

@description('Azure OpenAI embedding model version. Confirm availability in the selected region.')
param embeddingModelVersion string = '1'

@description('Azure OpenAI deployment SKU.')
param embeddingDeploymentSku string = 'GlobalStandard'

@description('Embedding deployment capacity in thousands of tokens per minute.')
@minValue(1)
param embeddingDeploymentCapacity int = 10

@description('Service Bus topic name used by the application.')
param serviceBusTopicName string = 'sharepoint-changes'

@description('Service Bus subscription name used by the worker.')
param serviceBusSubscriptionName string = 'search-indexer'

@description('Use a serverless Cosmos DB account. Switch to provisioned throughput once partitioning and RU use have been benchmarked on representative data.')
param graphRagCosmosServerless bool = true

@description('Let the API identity write to the graph, which administrator entity merges need. When false the API can only read it.')
param graphRagAllowAdminMerges bool = false

@description('Azure AI Search service SKU.')
@allowed([
  'basic'
  'standard'
  'standard2'
  'standard3'
])
param searchSku string = 'basic'


var namePrefix = toLower('${workloadName}-${environmentName}')
var compactPrefix = replace(namePrefix, '-', '')
var resourceTags = union(tags, {
  workload: workloadName
  environment: environmentName
  managedBy: 'Bicep'
})
var uniqueSuffix = uniqueString(namePrefix, subscription().subscriptionId, resourceGroup().id)
var serviceBusNamespaceName = take(toLower('${namePrefix}-sb-${uniqueSuffix}'), 50)
var searchServiceName = take(toLower('${namePrefix}-search-${uniqueSuffix}'), 60)
var openAiAccountName = take(toLower('${namePrefix}-openai-${uniqueSuffix}'), 64)
var documentIntelligenceAccountName = take(toLower('${namePrefix}-docintel-${uniqueSuffix}'), 64)
var contentSafetyAccountName = take(toLower('${namePrefix}-safety-${uniqueSuffix}'), 64)
var containerRegistryName = '${compactPrefix}cr${uniqueSuffix}'
var logAnalyticsWorkspaceName = take(toLower('${namePrefix}-logs-${uniqueSuffix}'), 63)
// Keep the workload/environment visible and retain a suffix within Storage's 24-character limit.
var graphCosmosAccountName = take(toLower('${namePrefix}-graph-${uniqueSuffix}'), 44)
var storageAccountName = '${compactPrefix}${take(uniqueSuffix, 4)}'

resource staticWebApp 'Microsoft.Web/staticSites@2024-11-01' = {
  name: '${namePrefix}-web-${uniqueSuffix}'
  location: staticWebAppLocation
  tags: resourceTags
  sku: {
    name: staticWebAppSku
    tier: staticWebAppSku
  }
  properties: {
    allowConfigFileUpdates: true
    buildProperties: {
      skipGithubActionWorkflowGeneration: true
    }
  }
}

resource containerRegistry 'Microsoft.ContainerRegistry/registries@2023-07-01' = {
  name: containerRegistryName
  location: location
  tags: resourceTags
  sku: {
    name: 'Basic'
  }
  properties: {
    adminUserEnabled: false
    publicNetworkAccess: 'Enabled'
  }
}

resource logAnalyticsWorkspace 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: logAnalyticsWorkspaceName
  location: location
  tags: resourceTags
  properties: {
    features: {
      enableLogAccessUsingOnlyResourcePermissions: true
    }
    publicNetworkAccessForIngestion: 'Enabled'
    publicNetworkAccessForQuery: 'Enabled'
    retentionInDays: 30
    sku: {
      name: 'PerGB2018'
    }
  }
}

resource applicationInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: '${namePrefix}-insights-${take(uniqueSuffix, 6)}'
  location: location
  kind: 'web'
  tags: resourceTags
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalyticsWorkspace.id
  }
}

resource uploadStorage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: storageAccountName
  location: location
  tags: resourceTags
  kind: 'StorageV2'
  sku: { name: 'Standard_LRS' }
  properties: {
    allowBlobPublicAccess: false
    allowSharedKeyAccess: true
    minimumTlsVersion: 'TLS1_2'
    publicNetworkAccess: 'Enabled'
  }
}

resource uploadBlobService 'Microsoft.Storage/storageAccounts/blobServices@2023-05-01' = {
  parent: uploadStorage
  name: 'default'
}

resource uploadContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: uploadBlobService
  name: 'chat-uploads'
  properties: { publicAccess: 'None' }
}

// Graph RAG snapshot archive: the versioned source the graph projection is rebuilt from.
resource graphSnapshotContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: uploadBlobService
  name: 'graph-snapshots'
  properties: { publicAccess: 'None' }
}

// Graph RAG projection. Local (key) auth is disabled: the applications use data-plane RBAC with managed
// identity, and only this template can change the containers.
resource graphCosmos 'Microsoft.DocumentDB/databaseAccounts@2024-11-15' = {
  name: graphCosmosAccountName
  location: location
  tags: resourceTags
  kind: 'GlobalDocumentDB'
  properties: {
    databaseAccountOfferType: 'Standard'
    locations: [{ locationName: location, failoverPriority: 0, isZoneRedundant: false }]
    consistencyPolicy: { defaultConsistencyLevel: 'Session' }
    capabilities: graphRagCosmosServerless ? [{ name: 'EnableServerless' }] : []
    disableLocalAuth: true
    disableKeyBasedMetadataWriteAccess: true
    minimalTlsVersion: 'Tls12'
    publicNetworkAccess: 'Enabled'
    backupPolicy: { type: 'Continuous', continuousModeProperties: { tier: 'Continuous7Days' } }
  }
}

resource graphDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2024-11-15' = {
  parent: graphCosmos
  name: 'graphrag'
  properties: { resource: { id: 'graphrag' } }
}

// Partitioned by the synthetic key tenantId|bucket. Only the fields queries filter on are indexed, and
// items expire only when they carry their own ttl (retracted assertions and tombstones).
var graphContainers = [
  { name: 'graphEntities', paths: ['/tenantId/?'] }
  { name: 'graphAssertions', paths: ['/tenantId/?', '/subjectEntityId/?', '/objectEntityId/?', '/predicate/?', '/status/?'] }
  { name: 'graphAssertionsByObject', paths: ['/tenantId/?', '/subjectEntityId/?', '/objectEntityId/?', '/predicate/?', '/status/?'] }
  { name: 'graphDocumentState', paths: ['/tenantId/?', '/status/?'] }
]

resource graphContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2024-11-15' = [for container in graphContainers: {
  parent: graphDatabase
  name: container.name
  properties: {
    resource: {
      id: container.name
      partitionKey: { paths: ['/partitionKey'], kind: 'Hash', version: 2 }
      defaultTtl: -1
      indexingPolicy: {
        indexingMode: 'consistent'
        automatic: true
        includedPaths: [for path in container.paths: { path: path }]
        excludedPaths: [{ path: '/*' }]
      }
    }
  }
}]

// Snapshots of dynamic-session working directories, and sandbox bindings, for isolated agent workspaces
// (agentWorkspaceMode in infra/ContainerApps). Kept even in Local mode, where it stays empty.
resource agentWorkspacesContainer 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-05-01' = {
  parent: uploadBlobService
  name: 'agent-workspaces'
  properties: { publicAccess: 'None' }
}

resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2024-01-01' = {
  name: serviceBusNamespaceName
  location: location
  tags: resourceTags
  sku: {
    name: 'Standard'
    tier: 'Standard'
  }
  properties: {
    disableLocalAuth: !allowServiceBusLocalAuth
    publicNetworkAccess: 'Enabled'
  }
}

resource serviceBusTopic 'Microsoft.ServiceBus/namespaces/topics@2024-01-01' = {
  parent: serviceBusNamespace
  name: serviceBusTopicName
  properties: {
    defaultMessageTimeToLive: 'P14D'
    enableBatchedOperations: true
    enablePartitioning: false
    maxSizeInMegabytes: 1024
    requiresDuplicateDetection: false
    status: 'Active'
    supportOrdering: true
  }
}

resource serviceBusSubscription 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2024-01-01' = {
  parent: serviceBusTopic
  name: serviceBusSubscriptionName
  properties: {
    deadLetteringOnFilterEvaluationExceptions: true
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P14D'
    enableBatchedOperations: true
    lockDuration: 'PT1M'
    maxDeliveryCount: 10
    requiresSession: false
    status: 'Active'
  }
}

// Graph indexing requests. Poison messages are dead-lettered by the worker with a reason code; the
// delivery count is a backstop for a worker that crashes while holding a message.
resource graphIndexingQueue 'Microsoft.ServiceBus/namespaces/queues@2024-01-01' = {
  parent: serviceBusNamespace
  name: 'graph-indexing'
  properties: {
    deadLetteringOnMessageExpiration: true
    defaultMessageTimeToLive: 'P14D'
    enableBatchedOperations: true
    lockDuration: 'PT5M'
    maxDeliveryCount: 10
    requiresDuplicateDetection: false
    requiresSession: false
    status: 'Active'
  }
}

resource apiServiceBusAuthorizationRule 'Microsoft.ServiceBus/namespaces/authorizationRules@2024-01-01' = if (allowServiceBusLocalAuth) {
  parent: serviceBusNamespace
  name: 'api-send'
  properties: {
    rights: [
      'Send'
    ]
  }
}

resource workerServiceBusAuthorizationRule 'Microsoft.ServiceBus/namespaces/authorizationRules@2024-01-01' = if (allowServiceBusLocalAuth) {
  parent: serviceBusNamespace
  name: 'worker-listen'
  properties: {
    rights: [
      'Listen'
    ]
  }
}

resource searchService 'Microsoft.Search/searchServices@2025-05-01' = {
  name: searchServiceName
  location: location
  tags: resourceTags
  sku: {
    name: searchSku
  }
  properties: {
    authOptions: {
      aadOrApiKey: {
        aadAuthFailureMode: 'http401WithBearerChallenge'
      }
    }
    disableLocalAuth: !allowSearchApiKeyAuth
    hostingMode: 'Default'
    partitionCount: 1
    publicNetworkAccess: 'enabled'
    replicaCount: 1
  }
}

resource openAiAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' = {
  name: openAiAccountName
  location: location
  tags: resourceTags
  kind: 'OpenAI'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: openAiAccountName
    disableLocalAuth: !allowOpenAiApiKeyAuth
    publicNetworkAccess: 'Enabled'
  }
}

resource embeddingDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAiAccount
  name: embeddingDeploymentName
  sku: {
    name: embeddingDeploymentSku
    capacity: embeddingDeploymentCapacity
  }
  properties: {
    model: {
      format: 'OpenAI'
      name: embeddingModelName
      version: embeddingModelVersion
    }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
  }
}

resource documentIntelligenceAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' = if (deployDocumentIntelligence) {
  name: documentIntelligenceAccountName
  location: location
  tags: resourceTags
  kind: 'FormRecognizer'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: documentIntelligenceAccountName
    disableLocalAuth: !allowDocumentIntelligenceApiKeyAuth
    publicNetworkAccess: 'Enabled'
  }
}

resource contentSafetyAccount 'Microsoft.CognitiveServices/accounts@2024-10-01' = if (deployContentSafety) {
  name: contentSafetyAccountName
  location: contentSafetyLocation
  tags: resourceTags
  kind: 'ContentSafety'
  sku: {
    name: 'S0'
  }
  properties: {
    customSubDomainName: contentSafetyAccountName
    disableLocalAuth: !allowContentSafetyApiKeyAuth
    publicNetworkAccess: 'Enabled'
  }
}

resource sql 'Microsoft.Sql/servers@2023-08-01' = {
  name: '${namePrefix}-sql-${uniqueSuffix}-${toLower(sqlLocation)}'
  location: sqlLocation
  tags: resourceTags
  properties: {
    version: '12.0'
    minimalTlsVersion: '1.2'
    publicNetworkAccess: 'Enabled'
    administrators: {
      administratorType: 'ActiveDirectory'
      principalType: sqlEntraAdministratorPrincipalType
      login: sqlEntraAdminName
      sid: sqlEntraAdminObjectId
      tenantId: tenant().tenantId
      azureADOnlyAuthentication: true
    }
  }
}
// Foundry and ACA use public outbound addresses. Authentication is still required.
resource azureSqlAccess 'Microsoft.Sql/servers/firewallRules@2023-08-01' = {
  parent: sql
  name: 'AllowAzureServices'
  properties: { startIpAddress: '0.0.0.0', endIpAddress: '0.0.0.0' }
}
resource database 'Microsoft.Sql/servers/databases@2023-08-01' = {
  parent: sql
  name: sqlDatabaseName
  location: sqlLocation
  tags: resourceTags
  sku: {
    name: sqlDatabaseSku
    tier: sqlDatabaseSku == 'Basic' ? 'Basic' : 'Standard'
  }
  properties: union({
    collation: 'SQL_Latin1_General_CP1_CI_AS'
  }, sqlDatabaseSku == 'Basic' ? { maxSizeBytes: 2147483648 } : {})
}
resource apiIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-api-runtime'
  location: location
  tags: resourceTags
}
resource workerIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-background-runtime'
  location: location
  tags: resourceTags
}
resource foundry 'Microsoft.CognitiveServices/accounts@2025-06-01' = {
  name: '${namePrefix}-foundry-${uniqueSuffix}'
  location: foundryLocation
  tags: resourceTags
  kind: 'AIServices'
  sku: { name: 'S0' }
  identity: { type: 'SystemAssigned' }
  properties: {
    customSubDomainName: '${namePrefix}-foundry-${uniqueSuffix}'
    allowProjectManagement: true
    disableLocalAuth: true
    publicNetworkAccess: 'Enabled'
  }
}
resource project 'Microsoft.CognitiveServices/accounts/projects@2025-06-01' = {
  parent: foundry
  name: 'sharepoint-agent'
  location: foundryLocation
  tags: resourceTags
  identity: { type: 'SystemAssigned' }
  properties: { displayName: 'SharePoint Agent' }
}
var acrPullRole = '7f951dda-4ed3-4680-a7ca-43fe172d538d'
resource projectPull 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, project.id, acrPullRole)
  scope: containerRegistry
  properties: {
    principalId: project.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', acrPullRole)
  }
}
output contentSafetyEndpoint string = contentSafetyAccount.?properties.endpoint ?? ''
output contentSafetyResourceId string = contentSafetyAccount.?id ?? ''
output serviceBusFullyQualifiedNamespace string = '${serviceBusNamespace.name}.servicebus.windows.net'
output serviceBusTopicName string = serviceBusTopic.name
output serviceBusSubscriptionName string = serviceBusSubscription.name
output apiServiceBusAuthorizationRuleName string = allowServiceBusLocalAuth ? apiServiceBusAuthorizationRule.name : ''
output workerServiceBusAuthorizationRuleName string = allowServiceBusLocalAuth ? workerServiceBusAuthorizationRule.name : ''
output searchEndpoint string = 'https://${searchService.name}.search.windows.net'
output openAiEndpoint string = openAiAccount.properties.endpoint
output embeddingDeploymentName string = embeddingDeployment.name
output documentIntelligenceEndpoint string = documentIntelligenceAccount.?properties.endpoint ?? ''
output containerRegistryName string = containerRegistry.name
output staticWebAppName string = staticWebApp.name
output staticWebAppUrl string = 'https://${staticWebApp.properties.defaultHostname}'
output containerRegistryLoginServer string = containerRegistry.properties.loginServer
// Ollaya's own environment (infra/Ollaya) sends its logs to the same workspace.
output logAnalyticsWorkspaceName string = logAnalyticsWorkspace.name
output applicationInsightsName string = applicationInsights.name
output applicationInsightsConnectionString string = applicationInsights.properties.ConnectionString
output uploadStorageServiceUri string = uploadStorage.properties.primaryEndpoints.blob
output uploadContainerName string = uploadContainer.name
output graphCosmosEndpoint string = graphCosmos.properties.documentEndpoint

resource chatDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = {
  parent: openAiAccount
  name: chatDeploymentName
  sku: { name: 'GlobalStandard', capacity: chatDeploymentCapacity }
  properties: {
    model: { format: 'OpenAI', name: chatModelName, version: chatModelVersion }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
  }
  dependsOn: [embeddingDeployment]
}

// Deployments on one account are created one at a time, so this waits for the chat deployment.
resource transcriptionDeployment 'Microsoft.CognitiveServices/accounts/deployments@2024-10-01' = if (deployTranscription) {
  parent: openAiAccount
  name: transcriptionDeploymentName
  sku: { name: 'GlobalStandard', capacity: transcriptionDeploymentCapacity }
  properties: {
    model: { format: 'OpenAI', name: transcriptionModelName, version: transcriptionModelVersion }
    versionUpgradeOption: 'OnceNewDefaultVersionAvailable'
  }
  dependsOn: [chatDeployment]
}

output hosting object = {
  sqlServerName: sql.name
  sqlServerFqdn: sql.properties.fullyQualifiedDomainName
  sqlDatabaseName: database.name
  apiIdentityId: apiIdentity.id
  apiClientId: apiIdentity.properties.clientId
  apiPrincipalId: apiIdentity.properties.principalId
  workerIdentityId: workerIdentity.id
  workerClientId: workerIdentity.properties.clientId
  workerPrincipalId: workerIdentity.properties.principalId
  foundryAccountName: foundry.name
  foundryProjectId: project.id
  foundryProjectEndpoint: 'https://${foundry.name}.services.ai.azure.com/api/projects/${project.name}'
}
output chatDeploymentName string = chatDeployment.name
output transcriptionDeploymentName string = deployTranscription ? transcriptionDeploymentName : ''
output openAiResourceId string = openAiAccount.id
output searchResourceId string = searchService.id
output storageResourceId string = uploadStorage.id

resource apiRegistry 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, apiIdentity.id, '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  scope: containerRegistry
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

resource workerRegistry 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, workerIdentity.id, '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  scope: containerRegistry
  properties: {
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

resource apiBus 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, apiIdentity.id, '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  scope: serviceBusNamespace
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  }
}

resource workerBus 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, workerIdentity.id, '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
  scope: serviceBusNamespace
  properties: {
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4f6d3b9b-027b-4f4c-9142-0e5a2a2247e0')
  }
}

resource apiIdentitySearch 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, apiIdentity.id, '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
  scope: searchService
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
  }
}

resource apiIdentitySearchService 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, apiIdentity.id, '7ca78c08-252a-4471-8644-bb5ff32d4ba0')
  scope: searchService
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7ca78c08-252a-4471-8644-bb5ff32d4ba0')
  }
}

resource apiIdentityOpenAI 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAiAccount.id, apiIdentity.id, '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  scope: openAiAccount
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  }
}

resource workerIdentitySearch 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, workerIdentity.id, '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
  scope: searchService
  properties: {
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '8ebe5a00-799e-43f5-93ac-243d3dce84a7')
  }
}

resource workerIdentitySearchService 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(searchService.id, workerIdentity.id, '7ca78c08-252a-4471-8644-bb5ff32d4ba0')
  scope: searchService
  properties: {
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7ca78c08-252a-4471-8644-bb5ff32d4ba0')
  }
}

resource workerIdentityOpenAI 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(openAiAccount.id, workerIdentity.id, '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  scope: openAiAccount
  properties: {
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd')
  }
}

// Graph RAG, least privilege. The worker writes the graph, the archive, and its own retries on the queue
// (it already receives from the namespace); the API reads the graph and writes it only when administrator
// merges are allowed. Cosmos data-plane roles are Cosmos SQL role assignments, not Azure RBAC.
var cosmosDataReaderRole = '00000000-0000-0000-0000-000000000001'
var cosmosDataContributorRole = '00000000-0000-0000-0000-000000000002'

resource workerGraphCosmos 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: graphCosmos
  name: guid(graphCosmosAccountName, workerIdentity.id, cosmosDataContributorRole)
  properties: {
    principalId: workerIdentity.properties.principalId
    roleDefinitionId: '${graphCosmos.id}/sqlRoleDefinitions/${cosmosDataContributorRole}'
    scope: graphCosmos.id
  }
}

resource apiGraphCosmos 'Microsoft.DocumentDB/databaseAccounts/sqlRoleAssignments@2024-11-15' = {
  parent: graphCosmos
  name: guid(graphCosmosAccountName, apiIdentity.id, graphRagAllowAdminMerges ? cosmosDataContributorRole : cosmosDataReaderRole)
  properties: {
    principalId: apiIdentity.properties.principalId
    roleDefinitionId: '${graphCosmos.id}/sqlRoleDefinitions/${graphRagAllowAdminMerges ? cosmosDataContributorRole : cosmosDataReaderRole}'
    scope: graphCosmos.id
  }
}

resource workerGraphArchive 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(uploadStorage.id, workerIdentity.id, 'graph-snapshots', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: graphSnapshotContainer
  properties: {
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}

resource workerGraphQueueSend 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(serviceBusNamespace.id, workerIdentity.id, 'graph-indexing', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  scope: graphIndexingQueue
  properties: {
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '69a216fc-b8fb-44d8-bc22-1f3c2cd27a39')
  }
}

resource apiBlob 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(uploadStorage.id, apiIdentity.id, 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  scope: uploadStorage
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'ba92f5b4-2d11-453d-a403-e96b0029c9fe')
  }
}

resource apiSafety 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployContentSafety) {
  name: guid(contentSafetyAccount.id, apiIdentity.id, 'a97b65f3-24c7-4388-baec-2e87135dc908')
  scope: contentSafetyAccount
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
  }
}

resource apiIdentityDocument 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployDocumentIntelligence) {
  name: guid(documentIntelligenceAccount.id, apiIdentity.id, 'a97b65f3-24c7-4388-baec-2e87135dc908')
  scope: documentIntelligenceAccount
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
  }
}

resource workerIdentityDocument 'Microsoft.Authorization/roleAssignments@2022-04-01' = if (deployDocumentIntelligence) {
  name: guid(documentIntelligenceAccount.id, workerIdentity.id, 'a97b65f3-24c7-4388-baec-2e87135dc908')
  scope: documentIntelligenceAccount
  properties: {
    principalId: workerIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'a97b65f3-24c7-4388-baec-2e87135dc908')
  }
}
