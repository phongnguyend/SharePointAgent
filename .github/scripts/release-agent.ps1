. "$PSScriptRoot/deployment-helpers.ps1"
# Resolve the deployment identity independently of the configured SQL administrator.
$token = az account get-access-token --query accessToken -o tsv
$payload = $token.Split('.')[1].Replace('-', '+').Replace('_', '/')
$payload = $payload.PadRight($payload.Length + (4 - $payload.Length % 4) % 4, '=')
$claims = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json
$deploymentPrincipalId = ([guid]$claims.oid).ToString()
Grant-Role $deploymentPrincipalId 'eadc314b-1a2d-4efa-be10-5d325db5065e' $hosting.foundryProjectId
$connection = @{ properties = @{
  category = 'CustomKeys'
  authType = 'CustomKeys'
  target = 'https://sharepoint-agent.invalid'
  credentials = @{ keys = @{
    graphClientSecret = $env:SHAREPOINT_CLIENT_SECRET
    webhookClientState = $env:SHAREPOINT_CLIENT_STATE
    markItDownApiKey = $env:MARKITDOWN_API_KEY
  } }
} }
$path = Write-RequestBody 'foundry-secrets' $connection
try {
  az rest --method put --url "https://management.azure.com$($hosting.foundryProjectId)/connections/agent-secrets?api-version=2025-06-01" --body "@$path" --output none
} finally {
  Remove-Item -LiteralPath $path -Force
}
function Connection-Secret($Key) {
  return '$' + '{{connections.agent-secrets.credentials.' + $Key + '}}'
}
$environment = @{
  SqlServer__ConnectionString = "Server=tcp:$($hosting.sqlServerFqdn),1433;Database=$($hosting.sqlDatabaseName);Authentication=Active Directory Managed Identity;Encrypt=True;TrustServerCertificate=False;"
  SqlServer__AutoMigrate = 'false'
  SharePoint__TenantId = $env:SHAREPOINT_TENANT_ID
  SharePoint__ClientId = $env:SHAREPOINT_CLIENT_ID
  SharePoint__ClientSecret = (Connection-Secret 'graphClientSecret')
  SharePoint__ClientState = (Connection-Secret 'webhookClientState')
  SharePoint__SiteHostname = $env:SHAREPOINT_SITE_HOSTNAME
  SharePoint__SitePath = $env:SHAREPOINT_SITE_PATH
  SharePoint__DocumentLibraryName = $env:SHAREPOINT_DOCUMENT_LIBRARY_NAME
  SharePoint__SubscriptionRenewalEnabled = 'false'
  SharePoint__NotificationUrl = "$($outputs.apiUrl.value.TrimEnd('/'))/api/sharepoint/webhook"
  AzureOpenAI__UsedManagedIdentity = 'true'
  AzureOpenAI__Endpoint = $outputs.openAiEndpoint.value
  AzureOpenAI__EmbeddingDeployment = $outputs.embeddingDeploymentName.value
  AzureOpenAI__ChatDeployment = $outputs.chatDeploymentName.value
  AzureSearch__UsedManagedIdentity = 'true'
  AzureSearch__Endpoint = $outputs.searchEndpoint.value
  Uploads__UsedManagedIdentity = 'true'
  Uploads__ServiceUri = $outputs.uploadStorageServiceUri.value
  Uploads__ContainerName = $outputs.uploadContainerName.value
  MarkItDown__Endpoint = $outputs.markItDownEndpoint.value
  MarkItDown__ApiKey = (Connection-Secret 'markItDownApiKey')
  ContentSafety__Enabled = ([bool]$settings.deployContentSafety.value).ToString().ToLowerInvariant()
  ContentSafety__UseManagedIdentity = 'true'
  ContentSafety__Endpoint = $outputs.contentSafetyEndpoint.value
}
$definition = @{
  kind = 'hosted'
  cpu = '1'
  memory = '2Gi'
  container_configuration = @{ image = "$($outputs.containerRegistryLoginServer.value)/agenthost:$env:IMAGE_TAG" }
  protocol_versions = @(@{ protocol = 'invocations'; version = '2.0.0' })
  environment_variables = $environment
}
$agent = $null
try {
  $agent = Invoke-Foundry 'GET' 'agents/sharepoint-agent'
} catch {
  if ([int]$_.Exception.Response.StatusCode -ne 404) {
    throw
  }
}
if ($null -eq $agent) {
  $null = Invoke-Foundry 'POST' 'agents' @{ name = 'sharepoint-agent'; definition = $definition }
  $version = '1'
} else {
  $created = Invoke-Foundry 'POST' 'agents/sharepoint-agent/versions' @{ definition = $definition }
  $version = [string]$created.version
}
if ([string]::IsNullOrWhiteSpace($version)) {
  throw 'Foundry did not return a version.'
}
for ($attempt = 0; $attempt -lt 60; $attempt++) {
  $agent = Invoke-Foundry 'GET' 'agents/sharepoint-agent'
  $principalId = $agent.instance_identity.principal_id
  $clientId = $agent.instance_identity.client_id
  if ($principalId -and $clientId) {
    break
  }
  Start-Sleep -Seconds 5
}
if (-not $principalId -or -not $clientId) {
  throw 'Foundry execution identity was not provisioned.'
}
Write-Host "Agent SQL client ID: $clientId. Configure database access manually in SQL; see infra/README.md#configure-runtime-sql-access-manually."
Grant-Role $principalId '5e0bd9bd-7b93-4f28-af87-19fc36ad61bd' $outputs.openAiResourceId.value
Grant-Role $principalId '8ebe5a00-799e-43f5-93ac-243d3dce84a7' $outputs.searchResourceId.value
Grant-Role $principalId '7ca78c08-252a-4471-8644-bb5ff32d4ba0' $outputs.searchResourceId.value
Grant-Role $principalId 'ba92f5b4-2d11-453d-a403-e96b0029c9fe' $outputs.storageResourceId.value
if ($outputs.contentSafetyResourceId.value) {
  Grant-Role $principalId 'a97b65f3-24c7-4388-baec-2e87135dc908' $outputs.contentSafetyResourceId.value
}
for ($attempt = 0; $attempt -lt 90; $attempt++) {
  $state = Invoke-Foundry 'GET' "agents/sharepoint-agent/versions/$version"
  if ($state.status -eq 'active') {
    break
  }
  if ($state.status -in @('failed', 'deleted', 'deleting')) {
    throw "Foundry version $version entered state $($state.status)."
  }
  Start-Sleep -Seconds 10
}
if ($state.status -ne 'active') {
  throw 'Timed out waiting for the Foundry version.'
}
$null = Invoke-Foundry 'PATCH' 'agents/sharepoint-agent' @{
  agent_endpoint = @{
    version_selector = @{
      version_selection_rules = @(@{ type = 'FixedRatio'; agent_version = $version; traffic_percentage = 100 })
    }
    protocol_configuration = @{ invocations = @{} }
  }
} 'application/merge-patch+json'
"FOUNDRY_VERSION=$version" >> $env:GITHUB_ENV
