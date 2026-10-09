$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
. "$PSScriptRoot/sandbox-host-image.ps1"

$apiVersion = '2026-07-01'
$outputs = az deployment group show --resource-group $env:RESOURCE_GROUP --name "infra-$env:DEPLOY_ENVIRONMENT" --query properties.outputs -o json | ConvertFrom-Json
if (-not $outputs.containerRegistryName.value) {
  throw 'Run Deploy infrastructure before releasing Dynamic Sessions.'
}
. "$PSScriptRoot/optional-deployments.ps1"
$poolName = Get-OptionalDeploymentOutput 'dynamic-sessions' 'dynamicSessionsPoolName'
$endpoint = Get-DynamicSessionsPoolEndpoint
if (-not $poolName) {
  throw 'Run Deploy Dynamic Sessions Infrastructure before releasing Dynamic Sessions.'
}

$image = Publish-SandboxHostImage $outputs

# Merge Patch replaces arrays whole, so the pool's own container definition is edited and sent back
# with its registry credentials, keeping settings the infrastructure owns.
$poolId = az resource show --resource-group $env:RESOURCE_GROUP --name $poolName `
  --resource-type Microsoft.App/sessionPools --query id -o tsv
$url = "https://management.azure.com$poolId`?api-version=$apiVersion"
$pool = az rest --method get --url $url -o json | ConvertFrom-Json -AsHashtable
$template = $pool.properties.customContainerTemplate
$containers = @($template.containers)
if ($containers.Count -ne 1) {
  throw "Expected one container in session pool $poolName. Run Deploy Dynamic Sessions Infrastructure first."
}
$containers[0].image = $image
# Same values as the infrastructure template: the pool authenticates callers and its ingress allows 240 seconds.
$settings = @(
  @{ name = 'Sandbox__RequireApiKey'; value = 'false' }
  @{ name = 'Sandbox__MaxTimeoutSeconds'; value = '220' }
)
$containers[0].env = @(@($containers[0].env) | Where-Object { $_ -and $_.name -notin $settings.name }) + $settings
$containers[0].probes = @(
  @{ type = 'Liveness'; httpGet = @{ path = '/health'; port = 8080 }; periodSeconds = 10; failureThreshold = 3 }
  @{ type = 'Startup'; httpGet = @{ path = '/health'; port = 8080 }; periodSeconds = 5; failureThreshold = 30 }
)
$body = @{ properties = @{ customContainerTemplate = @{
  containers = $containers
  ingress = @{ targetPort = 8080 }
  registryCredentials = $template.registryCredentials
} } }
$path = "$env:RUNNER_TEMP/dynamic-sessions.request.json"
$body | ConvertTo-Json -Depth 30 | Set-Content $path
try {
  az rest --method patch --url $url --body "@$path" --output none
} finally {
  Remove-Item -LiteralPath $path -Force
}

for ($attempt = 0; $attempt -lt 60; $attempt++) {
  $state = az rest --method get --url $url --query properties.provisioningState -o tsv
  if ($state -eq 'Succeeded') {
    break
  }
  if ($state -in @('Failed', 'Canceled')) {
    throw "Session pool entered state $state."
  }
  Start-Sleep -Seconds 10
}
if ($state -ne 'Succeeded') {
  throw 'Timed out waiting for the session pool update.'
}

# A fresh identifier allocates a new session, so this exercises the image the pool now runs. The
# deployment identity holds Session Executor from the infrastructure template; a new assignment can
# take a few minutes to reach the pool, which the health wait's retries absorb.
$identifier = "release-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
$token = az account get-access-token --resource https://dynamicsessions.io --query accessToken -o tsv
$headers = @{ Authorization = "Bearer $token" }
try {
  Test-SandboxHost $endpoint $headers $identifier 60
} finally {
  try {
    Invoke-RestMethod -Method Post "$($endpoint.TrimEnd('/'))/.management/stopSession?api-version=2025-02-02-preview&identifier=$identifier" -Headers $headers -TimeoutSec 30 | Out-Null
  } catch {
    Write-Warning "Could not stop smoke-test session ${identifier}: $($_.Exception.Message). It ends after the cooldown period."
  }
}

"## Dynamic Sessions deployed" >> $env:GITHUB_STEP_SUMMARY
"- Image: $image" >> $env:GITHUB_STEP_SUMMARY
"- Pool: $poolName" >> $env:GITHUB_STEP_SUMMARY
"- Endpoint: $endpoint" >> $env:GITHUB_STEP_SUMMARY
"Set the GitHub environment variable SANDBOX_HOST_IMAGE to this image so Deploy Dynamic Sessions Infrastructure keeps it instead of the hello image." >> $env:GITHUB_STEP_SUMMARY
