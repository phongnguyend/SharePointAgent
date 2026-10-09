$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
. "$PSScriptRoot/container-apps.ps1"

$shared = az deployment group show --resource-group $env:RESOURCE_GROUP --name "infra-$env:DEPLOY_ENVIRONMENT" --query properties.outputs -o json | ConvertFrom-Json
$PSNativeCommandUseErrorActionPreference = $false
$ollayaOutputs = az deployment group show --resource-group $env:RESOURCE_GROUP --name "ollaya-$env:DEPLOY_ENVIRONMENT" --query properties.outputs -o json 2>$null | ConvertFrom-Json
$PSNativeCommandUseErrorActionPreference = $true
if (-not $ollayaOutputs.ollayaContainerAppName.value) {
  throw 'Run Deploy Ollaya infrastructure before releasing Ollaya.'
}
$name = $ollayaOutputs.ollayaContainerAppName.value
$endpoint = $ollayaOutputs.ollayaEndpoint.value.TrimEnd('/')

# The model is baked into the image, so this pulls about 8 GB inside ACR Tasks rather than on the runner.
Push-Location backend/Ollaya
try {
  az acr build --registry $shared.containerRegistryName.value --platform linux/amd64 --no-logs --timeout 7200 `
    --image "ollaya:$env:IMAGE_TAG" --build-arg "OLLAYA_MODEL=$env:OLLAYA_MODEL" --file Dockerfile .
} finally {
  Pop-Location
}
$image = "$($shared.containerRegistryLoginServer.value)/ollaya:$env:IMAGE_TAG"

# Merge Patch replaces arrays whole, so the container and secret lists are edited and sent back in full.
$app = az containerapp show --resource-group $env:RESOURCE_GROUP --name $name -o json | ConvertFrom-Json -AsHashtable
$url = "https://management.azure.com$($app.id)"
$containers = @($app.properties.template.containers)
$container = @($containers | Where-Object { $_.name -eq 'ollaya' })
if ($container.Count -ne 1) {
  throw "Expected container ollaya in $name. Run Deploy Ollaya infrastructure first."
}
$container = $container[0]
$container.image = $image
$container.env = @(@($container.env) | Where-Object { $_ -and $_.name -ne 'OLLAYA_API_KEY' }) + @(@{ name = 'OLLAYA_API_KEY'; secretRef = 'ollaya-api-key' })
# GET / answers without the API key, replacing the hello image's probes on port 80.
$container.probes = @(
  @{ type = 'Liveness'; httpGet = @{ path = '/'; port = 11435 }; periodSeconds = 30 }
  @{ type = 'Readiness'; httpGet = @{ path = '/'; port = 11435 }; periodSeconds = 10 }
)
$existingSecrets = az rest --method post --url "$url/listSecrets?api-version=2025-01-01" -o json | ConvertFrom-Json -AsHashtable
$secrets = @(@($existingSecrets.value) | Where-Object { $_ -and $_.name -ne 'ollaya-api-key' }) + @(@{ name = 'ollaya-api-key'; value = $env:OLLAYA_API_KEY })
$body = @{ properties = @{
  configuration = @{ secrets = $secrets; ingress = @{ targetPort = 11435 } }
  template = @{ containers = $containers; revisionSuffix = "release-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT" }
} }
$path = Write-RequestBody 'ollaya' $body
try {
  az rest --method patch --url "$url`?api-version=2025-01-01" --body "@$path" --output none
} finally {
  Remove-Item -LiteralPath $path -Force
}
Wait-ContainerApp $name

# With minReplicas 0 the first request starts a GPU replica and pulls the image, which takes minutes.
$ready = $false
for ($attempt = 1; $attempt -le 80 -and -not $ready; $attempt++) {
  try {
    $text = Invoke-RestMethod "$endpoint/" -TimeoutSec 60
    $ready = "$text" -match 'Ollaya is running'
  } catch {
    Write-Host "Waiting for Ollaya to start (attempt $attempt): $($_.Exception.Message)"
  }
  if (-not $ready) {
    Start-Sleep -Seconds 15
  }
}
if (-not $ready) {
  throw "Ollaya did not answer at $endpoint/. Check console logs: az containerapp logs show --resource-group $env:RESOURCE_GROUP --name $name --type console --tail 100"
}

$headers = @{ Authorization = "Bearer $env:OLLAYA_API_KEY" }
try {
  Invoke-RestMethod "$endpoint/api/version" -TimeoutSec 30 | Out-Null
  throw 'Ollaya answered /api/version without the API key; check OLLAYA_API_KEY on the container.'
} catch {
  if ([int]$_.Exception.Response.StatusCode -ne 401) {
    throw
  }
}
$version = Invoke-RestMethod "$endpoint/api/version" -Headers $headers -TimeoutSec 30

# A real decision proves the model loads and runs on the GPU, not just that the server started.
$decision = @{
  model = $env:OLLAYA_MODEL
  state = 'The package arrived two weeks late and the box was damaged.'
  questions = @{ complaint = @{ type = 'noul'; instructions = 'Is this a complaint?' } }
} | ConvertTo-Json -Depth 10
$watch = [Diagnostics.Stopwatch]::StartNew()
$result = Invoke-RestMethod -Method Post "$endpoint/v1/systemone" -Headers $headers -ContentType 'application/json' -Body $decision -TimeoutSec 600
$watch.Stop()
if (-not $result.answers.complaint) {
  throw "Ollaya returned no answer for the smoke-test decision: $($result | ConvertTo-Json -Depth 10 -Compress)"
}

"## Ollaya deployed" >> $env:GITHUB_STEP_SUMMARY
"- Image: $image" >> $env:GITHUB_STEP_SUMMARY
"- Endpoint: $endpoint" >> $env:GITHUB_STEP_SUMMARY
"- Server version: $($version.version); model: $($result.model); smoke-test decision in $($watch.ElapsedMilliseconds) ms, including any model load" >> $env:GITHUB_STEP_SUMMARY
"Set the GitHub environment variable OLLAYA_IMAGE to this image so Deploy Ollaya infrastructure keeps it instead of the hello image." >> $env:GITHUB_STEP_SUMMARY
