param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'test')]
    [string] $EnvironmentName
)

$Component = 'WinnowNative'
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
. "$PSScriptRoot/winnow-native-settings.ps1"
. "$PSScriptRoot/container-apps.ps1"
Assert-WinnowNativeKey
$settings = Get-WinnowNativeSettings $EnvironmentName
$id = $settings.Id
$env:RESOURCE_GROUP = $settings.ResourceGroup
$shared = az deployment group show --resource-group $env:RESOURCE_GROUP --name "infra-$EnvironmentName" --query properties.outputs -o json | ConvertFrom-Json
$outputs = az deployment group show --resource-group $env:RESOURCE_GROUP --name $settings.Deployment --query properties.outputs -o json | ConvertFrom-Json
$name = $outputs."${id}ContainerAppName".value
if (-not $name -or -not $shared.containerRegistryName.value -or -not $shared.containerRegistryLoginServer.value) {
    throw 'Run the shared and service infrastructure workflows before releasing.'
}
$endpoint = $outputs."${id}Endpoint".value.TrimEnd('/')
$app = az containerapp show --resource-group $env:RESOURCE_GROUP --name $name -o json | ConvertFrom-Json -AsHashtable
if ($app.properties.runningStatus -ne 'Running') {
    throw 'Start the service with its Start or Stop workflow before releasing; release does not restart a deliberately stopped app.'
}

# Hash all build inputs, including source/model pins. Unchanged images can be reused.
$context = (Resolve-Path -LiteralPath "backend/SystemOne/$Component").Path
$hash = [Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
try {
    $files = Get-ChildItem -LiteralPath $context -File -Recurse | Where-Object { $_.Extension -ne '.md' -and $_.FullName -notmatch '[/\\]__pycache__[/\\]' } | Sort-Object FullName
    foreach ($file in $files) {
        $hash.AppendData([Text.Encoding]::UTF8.GetBytes([IO.Path]::GetRelativePath($context, $file.FullName).Replace('\', '/') + "`n"))
        $hash.AppendData([IO.File]::ReadAllBytes($file.FullName))
    }
    $tag = 'content-' + [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant().Substring(0, 16)
} finally {
    $hash.Dispose()
}
$image = "$($shared.containerRegistryLoginServer.value)/${id}:$tag"
$PSNativeCommandUseErrorActionPreference = $false
$existing = az acr repository show-tags --name $shared.containerRegistryName.value --repository $id --query "[?@=='$tag']" -o tsv 2>$null
$found = $LASTEXITCODE -eq 0 -and $existing -eq $tag
$PSNativeCommandUseErrorActionPreference = $true
if (-not $found -or $env:WINNOW_NATIVE_REBUILD -eq 'true') {
    $buildArguments = @()
    $pools = @(az acr agentpool list --registry $shared.containerRegistryName.value -o json | ConvertFrom-Json)
    $pool = $pools | Where-Object { $_.name -eq 'nativebuild' }
    if ($pool -and $pool.count -gt 0) {
        if ($pool.provisioningState -ne 'Succeeded') {
            throw 'Wait for the nativebuild pool to finish provisioning before releasing.'
        }
        $buildArguments = @('--agent-pool', 'nativebuild', '--build-arg', 'BUILD_JOBS=8')
        Write-Host 'Building on nativebuild (8 vCPU per S3 agent).'
    } else {
        Write-Host 'No running nativebuild pool; using the default ACR builder.'
    }
    # All compiler work and model downloads happen on ACR, never local Docker or the runner.
    Push-Location -LiteralPath $context
    try {
        az acr build --registry $shared.containerRegistryName.value --platform linux/amd64 --timeout 10800 `
            --image "${id}:$tag" @buildArguments --file Dockerfile .
    } finally {
        Pop-Location
    }
} else {
    Write-Host "Reusing $image."
}
# Deploy by immutable image digest, including when a content tag is explicitly rebuilt.
$digest = az acr repository show --name $shared.containerRegistryName.value --image "${id}:$tag" --query digest -o tsv
if ($digest -notmatch '^sha256:[a-f0-9]{64}$') {
    throw 'The registry did not return an image digest.'
}
$image = "$($shared.containerRegistryLoginServer.value)/${id}@$digest"
$containers = @($app.properties.template.containers)
$container = @($containers | Where-Object { $_.name -eq $id })
if ($container.Count -ne 1) {
    throw "Expected exactly one $id container in $name."
}
$container = $container[0]
$container.image = $image
$container.env = @(@($container.env) | Where-Object { $_ -and $_.name -ne $settings.KeyName }) + @(@{ name = $settings.KeyName; secretRef = $settings.SecretName })
$container.probes = @(
    @{ type = 'Startup'; httpGet = @{ path = '/health'; port = $settings.Port }; periodSeconds = 60; failureThreshold = 10; timeoutSeconds = 3 }
    @{ type = 'Liveness'; tcpSocket = @{ port = $settings.Port }; periodSeconds = 30 }
    @{ type = 'Readiness'; httpGet = @{ path = '/health'; port = $settings.Port }; periodSeconds = 10; timeoutSeconds = 3 }
)
$url = "https://management.azure.com$($app.id)"
$existingSecrets = az rest --method post --url "$url/listSecrets?api-version=2025-01-01" -o json | ConvertFrom-Json -AsHashtable
$secrets = @(@($existingSecrets.value) | Where-Object { $_ -and $_.name -ne $settings.SecretName }) + @(@{ name = $settings.SecretName; value = $env:WINNOW_NATIVE_API_KEY })
$body = @{ properties = @{
    configuration = @{ secrets = $secrets; ingress = @{ targetPort = $settings.Port } }
    template = @{ containers = $containers; revisionSuffix = "release-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT" }
} }
$path = Write-RequestBody $id $body
try {
    az rest --method patch --url "$url`?api-version=2025-01-01" --body "@$path" --output none
} finally {
    Remove-Item -LiteralPath $path -Force
}
Wait-ContainerApp $name

$headers = @{ Authorization = "Bearer $env:WINNOW_NATIVE_API_KEY" }
$ready = $false
for ($attempt = 0; $attempt -lt 60 -and -not $ready; $attempt++) {
    try {
        $health = Invoke-RestMethod "$endpoint/health" -Headers $headers -TimeoutSec 30
        $ready = $health.status -eq 'ok'
    } catch {
        Write-Host "Waiting for $Component health (attempt $attempt)."
    }
    if (-not $ready) {
        Start-Sleep -Seconds 10
    }
}
if (-not $ready) {
    throw "No healthy response from $endpoint/health. Check Container App console logs."
}
$decision = @{
    model = $settings.Model
    state = 'The package arrived two weeks late and the box was damaged.'
    questions = @{ complaint = @{ type = 'noul'; instructions = 'Is this a complaint?' } }
} | ConvertTo-Json -Depth 10
try {
    Invoke-RestMethod -Method Post "$endpoint/v1/systemone" -ContentType 'application/json' -Body $decision -TimeoutSec 30 | Out-Null
    throw 'The decision endpoint accepted a request without authentication.'
} catch {
    if ([int]$_.Exception.Response.StatusCode -ne 401) {
        throw
    }
}
# Measure cold/warm application inference separately from image startup.
$timings = @()
foreach ($pass in @('first', 'warm')) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $result = Invoke-RestMethod -Method Post "$endpoint/v1/systemone" -Headers $headers -ContentType 'application/json' -Body $decision -TimeoutSec 600
    $watch.Stop()
    $answer = $result.answers.complaint
    if ($answer.type -ne 'noul' -or $null -eq $answer.noul -or $answer.noul -lt 0 -or $answer.noul -gt 1) {
        throw 'The System One smoke test did not return a valid noul probability.'
    }
    $timings += "$pass=$($watch.ElapsedMilliseconds) ms"
}
"## $Component released" >> $env:GITHUB_STEP_SUMMARY
"- Image: $image" >> $env:GITHUB_STEP_SUMMARY
"- Endpoint: $endpoint" >> $env:GITHUB_STEP_SUMMARY
"- Decision smoke tests: $($timings -join ', ')" >> $env:GITHUB_STEP_SUMMARY
'- Health, missing-token rejection, and authenticated inference passed. This is not a load test.' >> $env:GITHUB_STEP_SUMMARY
