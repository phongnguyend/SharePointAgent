param([ValidateSet('Api', 'Background')][string]$Component)

. "$PSScriptRoot/deployment-helpers.ps1"
if ([string]::IsNullOrWhiteSpace($outputs.applicationInsightsConnectionString.value)) {
  throw 'Run Deploy infrastructure to provision Application Insights before releasing applications.'
}
$commonValues = @{
  Monitoring__OpenTelemetry__Exporter = 'AzureMonitor'
  Monitoring__OpenTelemetry__Environment = $env:DEPLOY_ENVIRONMENT
  Monitoring__OpenTelemetry__AzureMonitor__ConnectionString = $outputs.applicationInsightsConnectionString.value
  ASPNETCORE_ENVIRONMENT = 'Production'
  SqlServer__AutoMigrate = 'false'
  SharePoint__TenantId = $env:SHAREPOINT_TENANT_ID
  SharePoint__ClientId = $env:SHAREPOINT_CLIENT_ID
  SharePoint__SiteHostname = $env:SHAREPOINT_SITE_HOSTNAME
  SharePoint__SitePath = $env:SHAREPOINT_SITE_PATH
  SharePoint__DocumentLibraryName = $env:SHAREPOINT_DOCUMENT_LIBRARY_NAME
  SharePoint__NotificationUrl = "$($outputs.apiUrl.value.TrimEnd('/'))/api/sharepoint/webhook"
  ServiceBus__UseManagedIdentity = 'true'
  ServiceBus__FullyQualifiedNamespace = $outputs.serviceBusFullyQualifiedNamespace.value
  ServiceBus__TopicName = $outputs.serviceBusTopicName.value
  ServiceBus__SubscriptionName = $outputs.serviceBusSubscriptionName.value
  AzureSearch__UseManagedIdentity = 'true'
  AzureSearch__Endpoint = $outputs.searchEndpoint.value
  AzureOpenAI__UseManagedIdentity = 'true'
  AzureOpenAI__Endpoint = $outputs.openAiEndpoint.value
  AzureOpenAI__EmbeddingDeployment = $outputs.embeddingDeploymentName.value
  AzureOpenAI__ChatDeployment = $outputs.chatDeploymentName.value
  KnowledgeGraph__Extraction__Deployment = $outputs.chatDeploymentName.value
  AzureOpenAI__TranscriptionDeployment = [string]$outputs.transcriptionDeploymentName.value
  MarkItDown__Endpoint = $outputs.markItDownEndpoint.value
  PageIndex__Endpoint = [string]$outputs.pageIndexEndpoint.value
  Ollaya__Endpoint = (Get-OllayaEndpoint)
  DocumentIntelligence__UseManagedIdentity = 'true'
  DocumentIntelligence__Endpoint = [string]$outputs.documentIntelligenceEndpoint.value
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
if (-not [string]::IsNullOrWhiteSpace($env:JEV_API_KEY)) {
  $commonEnvironment += @{ name = 'Jev__ApiKey'; secretRef = 'jev-api-key' }
  $secrets += @{ name = 'jev-api-key'; value = $env:JEV_API_KEY }
} else {
  $commonEnvironment += @{ name = 'Jev__ApiKey'; value = '' }
}
# Optional until Ollaya is released; an empty Container Apps secret is rejected, so it is added only when set.
if (-not [string]::IsNullOrWhiteSpace($env:OLLAYA_API_KEY)) {
  $commonEnvironment += @{ name = 'Ollaya__ApiKey'; secretRef = 'ollaya-api-key' }
  $secrets += @{ name = 'ollaya-api-key'; value = $env:OLLAYA_API_KEY }
}
if ($Component -eq 'Api') {
  . "$PSScriptRoot/document-signing-settings.ps1"
  $signing = Get-DocumentSigningSettings
  $apiValues = @{
    AZURE_CLIENT_ID = $hosting.apiClientId
    SqlServer__ConnectionString = "Server=tcp:$($hosting.sqlServerFqdn),1433;Database=$($hosting.sqlDatabaseName);Authentication=Active Directory Managed Identity;User Id=$($hosting.apiClientId);Encrypt=True;TrustServerCertificate=False;"
    ChatAgent__DefaultMode = 'Foundry'
    ChatAgent__Foundry__Endpoint = "$($hosting.foundryProjectEndpoint)/agents/sharepoint-agent/endpoint/protocols/invocations?api-version=v1"
    Uploads__UseManagedIdentity = 'true'
    Uploads__ServiceUri = $outputs.uploadStorageServiceUri.value
    Uploads__ContainerName = $outputs.uploadContainerName.value
    Cors__AllowedOrigins__0 = $env:FRONTEND_ORIGIN
    AppIdentity__BootstrapAdminEmails__0 = $env:BOOTSTRAP_ADMIN_EMAIL
    ContentSafety__Enabled = ([bool]$settings.deployContentSafety.value).ToString().ToLowerInvariant()
    ContentSafety__Endpoint = [string]$outputs.contentSafetyEndpoint.value
    ContentSafety__UseManagedIdentity = 'true'
    Speech__Endpoint = [string]$outputs.speechEndpoint.value
    Speech__ResourceId = [string]$outputs.speechResourceId.value
    Speech__UseManagedIdentity = 'true'
  }
  $apiEnvironment = $commonEnvironment + @($apiValues.GetEnumerator() | ForEach-Object { @{ name = $_.Key; value = [string]$_.Value } })
  $apiEnvironment += @{ name = 'WinnowNative__Endpoint'; value = (Get-OptionalDeploymentOutput 'winnownative' 'winnownativeEndpoint') }
  if (-not [string]::IsNullOrWhiteSpace($env:WINNOW_NATIVE_API_KEY)) {
    $apiEnvironment += @{ name = 'WinnowNative__ApiKey'; secretRef = 'winnownative-api-key' }
    $secrets += @{ name = 'winnownative-api-key'; value = $env:WINNOW_NATIVE_API_KEY }
  } else {
    $apiEnvironment += @{ name = 'WinnowNative__ApiKey'; value = '' }
  }
  $apiEnvironment += @{ name = 'LayaNative__Endpoint'; value = (Get-OptionalDeploymentOutput 'layanative' 'layanativeEndpoint') }
  if (-not [string]::IsNullOrWhiteSpace($env:LAYA_NATIVE_API_KEY)) {
    $apiEnvironment += @{ name = 'LayaNative__ApiKey'; secretRef = 'layanative-api-key' }
    $secrets += @{ name = 'layanative-api-key'; value = $env:LAYA_NATIVE_API_KEY }
  } else {
    $apiEnvironment += @{ name = 'LayaNative__ApiKey'; value = '' }
  }
  # Optional; when unset, the current values are kept.
  $workspaceValues = @{
    AgentWorkspace__DynamicSessions__PoolManagementEndpoint = (Get-DynamicSessionsPoolEndpoint)
    AgentWorkspace__Sandboxes__SandboxGroup = (Get-SandboxGroupName)
    AgentWorkspace__Sandboxes__DiskImageId = (Get-SandboxDiskImageId)
  }
  foreach ($setting in $workspaceValues.GetEnumerator()) {
    if (-not [string]::IsNullOrWhiteSpace($setting.Value)) {
      $apiEnvironment += @{ name = $setting.Key; value = $setting.Value.Trim() }
    }
  }
  $apiEnvironment += $signing.Environment
  $secrets += $signing.Secrets
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
