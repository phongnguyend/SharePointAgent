param([ValidateSet('Api', 'Background')][string]$Component)

. "$PSScriptRoot/deployment-helpers.ps1"
$commonValues = @{
  ASPNETCORE_ENVIRONMENT = 'Production'
  SqlServer__AutoMigrate = 'false'
  SharePoint__TenantId = $env:SHAREPOINT_TENANT_ID
  SharePoint__ClientId = $env:SHAREPOINT_CLIENT_ID
  SharePoint__SiteHostname = $env:SHAREPOINT_SITE_HOSTNAME
  SharePoint__SitePath = $env:SHAREPOINT_SITE_PATH
  SharePoint__DocumentLibraryName = $env:SHAREPOINT_DOCUMENT_LIBRARY_NAME
  SharePoint__NotificationUrl = "$($outputs.apiUrl.value.TrimEnd('/'))/api/sharepoint/webhook"
  ServiceBus__Enabled = 'true'
  ServiceBus__UsedManagedIdentity = 'true'
  ServiceBus__FullyQualifiedNamespace = $outputs.serviceBusFullyQualifiedNamespace.value
  ServiceBus__TopicName = $outputs.serviceBusTopicName.value
  ServiceBus__SubscriptionName = $outputs.serviceBusSubscriptionName.value
  AzureSearch__UsedManagedIdentity = 'true'
  AzureSearch__Endpoint = $outputs.searchEndpoint.value
  AzureOpenAI__UsedManagedIdentity = 'true'
  AzureOpenAI__Endpoint = $outputs.openAiEndpoint.value
  AzureOpenAI__EmbeddingDeployment = $outputs.embeddingDeploymentName.value
  AzureOpenAI__ChatDeployment = $outputs.chatDeploymentName.value
  MarkItDown__Endpoint = $outputs.markItDownEndpoint.value
  DocumentIntelligence__UsedManagedIdentity = 'true'
  DocumentIntelligence__Endpoint = [string]$outputs.documentIntelligenceEndpoint.value
  OfficeCli__Enabled = 'false'
}
$commonEnvironment = @($commonValues.GetEnumerator() | ForEach-Object { @{ name = $_.Key; value = [string]$_.Value } }) + @(
  @{ name = 'SharePoint__ClientSecret'; secretRef = 'graph-client-secret' }
  @{ name = 'SharePoint__ClientState'; secretRef = 'webhook-client-state' }
  @{ name = 'MarkItDown__ApiKey'; secretRef = 'markitdown-api-key' }
)
$secrets = @(
  @{ name = 'graph-client-secret'; value = $env:SHAREPOINT_CLIENT_SECRET }
  @{ name = 'webhook-client-state'; value = $env:SHAREPOINT_CLIENT_STATE }
  @{ name = 'markitdown-api-key'; value = $env:MARKITDOWN_API_KEY }
)
if ($Component -eq 'Api') {
  $apiValues = @{
    AZURE_CLIENT_ID = $hosting.apiClientId
    SqlServer__ConnectionString = "Server=tcp:$($hosting.sqlServerFqdn),1433;Database=$($hosting.sqlDatabaseName);Authentication=Active Directory Managed Identity;User Id=$($hosting.apiClientId);Encrypt=True;TrustServerCertificate=False;"
    ChatAgent__Mode = 'Foundry'
    ChatAgent__Foundry__Endpoint = "$($hosting.foundryProjectEndpoint)/agents/sharepoint-agent/endpoint/protocols/invocations?api-version=v1"
    ChatAgent__Foundry__ManagedIdentityClientId = $hosting.apiClientId
    Uploads__UsedManagedIdentity = 'true'
    Uploads__ServiceUri = $outputs.uploadStorageServiceUri.value
    Uploads__ContainerName = $outputs.uploadContainerName.value
    Cors__AllowedOrigins__0 = $env:FRONTEND_ORIGIN
    AppIdentity__BootstrapAdminEmails__0 = $env:BOOTSTRAP_ADMIN_EMAIL
    ContentSafety__Enabled = ([bool]$settings.deployContentSafety.value).ToString().ToLowerInvariant()
    ContentSafety__Endpoint = [string]$outputs.contentSafetyEndpoint.value
    ContentSafety__UseManagedIdentity = 'true'
    ContentSafety__ManagedIdentityClientId = $hosting.apiClientId
  }
  $apiEnvironment = $commonEnvironment + @($apiValues.GetEnumerator() | ForEach-Object { @{ name = $_.Key; value = [string]$_.Value } })
  Grant-Role $hosting.apiPrincipalId '53ca6127-db72-4b80-b1b0-d745d6d5456d' $hosting.foundryProjectId
  Update-ContainerApp $outputs.apiContainerAppName.value 'api' $apiEnvironment $secrets 8080
  Wait-ContainerApp $outputs.apiContainerAppName.value
  Invoke-RestMethod "$($outputs.apiUrl.value.TrimEnd('/'))/health" -MaximumRetryCount 12 -RetryIntervalSec 10 -TimeoutSec 30 | Out-Null
} else {
  $backgroundEnvironment = $commonEnvironment + @(
    @{ name = 'AZURE_CLIENT_ID'; value = $hosting.workerClientId }
    @{ name = 'SqlServer__ConnectionString'; value = "Server=tcp:$($hosting.sqlServerFqdn),1433;Database=$($hosting.sqlDatabaseName);Authentication=Active Directory Managed Identity;User Id=$($hosting.workerClientId);Encrypt=True;TrustServerCertificate=False;" }
  )
  Update-ContainerApp $outputs.workerContainerAppName.value 'background' $backgroundEnvironment $secrets
  Wait-ContainerApp $outputs.workerContainerAppName.value
}
