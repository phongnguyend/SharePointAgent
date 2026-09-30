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

@description('Azure AI Search service SKU.')
@allowed([
  'basic'
  'standard'
  'standard2'
  'standard3'
])
param searchSku string = 'basic'

@description('Use release images and runtime settings. False provisions hello containers without application secrets.')
param deployApplicationImages bool = false

@description('API image reference. Set to an existing ACR tag or digest and enable deployApplicationImages for runtime settings.')
param apiImage string = 'mcr.microsoft.com/k8se/quickstart:latest'
@description('Background worker image reference; defaults to the public hello image.')
param backgroundImage string = 'mcr.microsoft.com/k8se/quickstart:latest'
@description('MarkItDown image reference; defaults to the public hello image.')
param markItDownImage string = 'mcr.microsoft.com/k8se/quickstart:latest'
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
var containerAppsEnvironmentName = take(toLower('${namePrefix}-cae-${uniqueSuffix}'), 60)
// Keep the workload/environment visible and retain a suffix within Storage's 24-character limit.
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

resource containerAppsEnvironment 'Microsoft.App/managedEnvironments@2025-01-01' = {
  name: containerAppsEnvironmentName
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
resource markItDownIdentity 'Microsoft.ManagedIdentity/userAssignedIdentities@2023-01-31' = {
  name: '${namePrefix}-markitdown-pull'
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
output containerAppsEnvironmentName string = containerAppsEnvironment.name
output uploadStorageServiceUri string = uploadStorage.properties.primaryEndpoints.blob
output uploadContainerName string = uploadContainer.name

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
output openAiResourceId string = openAiAccount.id
output searchResourceId string = searchService.id
output storageResourceId string = uploadStorage.id

var apiName = '${namePrefix}-api-${take(uniqueSuffix, 6)}'
var workerName = '${namePrefix}-wrk-${take(uniqueSuffix, 6)}'
var markItDownName = '${namePrefix}-md-${take(uniqueSuffix, 6)}'
var sqlServerFqdn = sql.properties.fullyQualifiedDomainName
var foundryEndpoint = 'https://${foundry.name}.services.ai.azure.com/api/projects/${project.name}/agents/sharepoint-agent/endpoint/protocols/invocations?api-version=v1'

var markItDownUrl = 'https://${markItDownName}.${containerAppsEnvironment.properties.defaultDomain}'
var apiEndpoint = 'https://${apiName}.${containerAppsEnvironment.properties.defaultDomain}'
var commonEnv = [
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
  { name: 'ServiceBus__FullyQualifiedNamespace', value: '${serviceBusNamespace.name}.servicebus.windows.net' }
  { name: 'ServiceBus__TopicName', value: serviceBusTopicName }
  { name: 'ServiceBus__SubscriptionName', value: serviceBusSubscriptionName }
  { name: 'AzureSearch__UsedManagedIdentity', value: 'true' }
  { name: 'AzureSearch__Endpoint', value: 'https://${searchService.name}.search.windows.net' }
  { name: 'AzureOpenAI__UsedManagedIdentity', value: 'true' }
  { name: 'AzureOpenAI__Endpoint', value: openAiAccount.properties.endpoint }
  { name: 'AzureOpenAI__EmbeddingDeployment', value: embeddingDeploymentName }
  { name: 'AzureOpenAI__ChatDeployment', value: chatDeploymentName }
  { name: 'MarkItDown__Endpoint', value: markItDownUrl }
  { name: 'MarkItDown__ApiKey', secretRef: 'markitdown-api-key' }
  { name: 'DocumentIntelligence__UsedManagedIdentity', value: 'true' }
  { name: 'DocumentIntelligence__Endpoint', value: documentIntelligenceAccount.?properties.endpoint ?? '' }
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
resource api 'Microsoft.App/containerApps@2025-01-01' = {
  name: apiName
  location: location
  tags: resourceTags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${apiIdentity.id}': {} } }
  properties: {
    environmentId: containerAppsEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      ingress: { external: true, allowInsecure: false, targetPort: deployApplicationImages ? 8080 : 80, transport: 'http' }
      registries: [{ server: containerRegistry.properties.loginServer, identity: apiIdentity.id }]
      secrets: deployApplicationImages ? secrets : []
    }
    template: {
      containers: [{
        name: 'api'
        image: apiImage
        env: deployApplicationImages ? concat(commonEnv, [
          { name: 'AZURE_CLIENT_ID', value: apiIdentity.properties.clientId }
          { name: 'SqlServer__ConnectionString', value: 'Server=tcp:${sqlServerFqdn},1433;Database=${sqlDatabaseName};Authentication=Active Directory Managed Identity;User Id=${apiIdentity.properties.clientId};Encrypt=True;TrustServerCertificate=False;' }
          { name: 'ChatAgent__Mode', value: 'Foundry' }
          { name: 'ChatAgent__Foundry__Endpoint', value: foundryEndpoint }
          { name: 'ChatAgent__Foundry__ManagedIdentityClientId', value: apiIdentity.properties.clientId }
          { name: 'Uploads__UsedManagedIdentity', value: 'true' }
          { name: 'Uploads__ServiceUri', value: uploadStorage.properties.primaryEndpoints.blob }
          { name: 'Uploads__ContainerName', value: 'chat-uploads' }
          { name: 'Cors__AllowedOrigins__0', value: frontendOrigin }
          { name: 'AppIdentity__BootstrapAdminEmails__0', value: bootstrapAdminEmail }
          { name: 'ContentSafety__Enabled', value: string(deployContentSafety) }
          { name: 'ContentSafety__Endpoint', value: contentSafetyAccount.?properties.endpoint ?? '' }
          { name: 'ContentSafety__UseManagedIdentity', value: 'true' }
          { name: 'ContentSafety__ManagedIdentityClientId', value: apiIdentity.properties.clientId }
        ]) : []
        resources: { cpu: json('2.0'), memory: '4Gi' }
        probes: [{ type: 'Readiness', httpGet: { path: deployApplicationImages ? '/health' : '/', port: deployApplicationImages ? 8080 : 80 }, periodSeconds: 10 }]
      }]
      scale: { minReplicas: 1, maxReplicas: 3 }
    }
  }
  dependsOn: [markItDown, apiRegistry, apiBus, apiIdentitySearch, apiIdentitySearchService, apiIdentityOpenAI, apiBlob, apiSafety, apiIdentityDocument]
}
resource background 'Microsoft.App/containerApps@2025-01-01' = {
  name: workerName
  location: location
  tags: resourceTags
  identity: { type: 'UserAssigned', userAssignedIdentities: { '${workerIdentity.id}': {} } }
  properties: {
    environmentId: containerAppsEnvironment.id
    configuration: {
      activeRevisionsMode: 'Single'
      registries: [{ server: containerRegistry.properties.loginServer, identity: workerIdentity.id }]
      secrets: deployApplicationImages ? secrets : []
    }
    template: {
      containers: [{
        name: 'background'
        image: backgroundImage
        env: deployApplicationImages ? concat(commonEnv, [
          { name: 'AZURE_CLIENT_ID', value: workerIdentity.properties.clientId }
          { name: 'SqlServer__ConnectionString', value: 'Server=tcp:${sqlServerFqdn},1433;Database=${sqlDatabaseName};Authentication=Active Directory Managed Identity;User Id=${workerIdentity.properties.clientId};Encrypt=True;TrustServerCertificate=False;' }
        ]) : []
        resources: { cpu: json('1.0'), memory: '2Gi' }
      }]
      // A continuous Service Bus receiver and scheduled reconciliation worker.
      scale: { minReplicas: 1, maxReplicas: 1 }
    }
  }
  dependsOn: [markItDown, workerRegistry, workerBus, workerIdentitySearch, workerIdentitySearchService, workerIdentityOpenAI, workerIdentityDocument]
}

resource apiRegistry 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, apiIdentity.id, '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  scope: containerRegistry
  properties: {
    principalId: apiIdentity.properties.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  }
}

resource markItDownRegistry 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(containerRegistry.id, markItDownIdentity.id, '7f951dda-4ed3-4680-a7ca-43fe172d538d')
  scope: containerRegistry
  properties: {
    principalId: markItDownIdentity.properties.principalId
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

output apiContainerAppName string = apiName
output workerContainerAppName string = workerName
output markItDownContainerAppName string = markItDownName
output apiUrl string = apiEndpoint
output markItDownEndpoint string = markItDownUrl

// Hosted environment variables reference this connection instead of containing raw secrets.
resource foundrySecrets 'Microsoft.CognitiveServices/accounts/projects/connections@2025-06-01' = if (deployApplicationImages) {
  parent: project
  name: 'agent-secrets'
  properties: {
    category: 'CustomKeys'
    authType: 'CustomKeys'
    target: 'https://sharepoint-agent.invalid'
    credentials: {
      keys: {
        graphClientSecret: sharePointClientSecret
        webhookClientState: sharePointClientState
        markItDownApiKey: markItDownApiKey
      }
    }
  }
}
