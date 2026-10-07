param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'test')]
    [string] $EnvironmentName,

    [Parameter(Mandatory)]
    [ValidateSet('Api', 'Background', 'MarkItDown', 'PageIndex')]
    [string] $Component,

    [Parameter(Mandatory)]
    [ValidateSet('0.25', '0.5', '1', '2', '4')]
    [string] $Cpu,

    [string] $ResourceGroup
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$memory = @{ '0.25' = '0.5Gi'; '0.5' = '1Gi'; '1' = '2Gi'; '2' = '4Gi'; '4' = '8Gi' }[$Cpu]
$settings = (Get-Content -LiteralPath (Join-Path $PSScriptRoot "../../infra/parameters.$EnvironmentName.json") -Raw | ConvertFrom-Json).parameters
if ($settings.environmentName.value -ne $EnvironmentName) {
    throw 'Parameter environment does not match the selected environment.'
}
if ([string]::IsNullOrWhiteSpace($ResourceGroup)) {
    $ResourceGroup = "rg-$($settings.workloadName.value)-$EnvironmentName"
}
$outputs = az deployment group show --resource-group $ResourceGroup --name "infra-$EnvironmentName" --query properties.outputs -o json | ConvertFrom-Json

function Write-CapacitySummary([string] $Details) {
    Write-Host $Details
    if ($env:GITHUB_STEP_SUMMARY) {
        "## $Component capacity ($EnvironmentName)`n$Details" | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    }
}

$outputName = @{ Api = 'apiContainerAppName'; Background = 'workerContainerAppName'; MarkItDown = 'markItDownContainerAppName'; PageIndex = 'pageIndexContainerAppName' }[$Component]
$appName = $outputs.$outputName.value
if ([string]::IsNullOrWhiteSpace($appName)) {
    throw 'Container App output is missing. Run Deploy infrastructure first.'
}
$app = az containerapp show --resource-group $ResourceGroup --name $appName -o json | ConvertFrom-Json
$containerName = $Component.ToLowerInvariant()
$containers = @($app.properties.template.containers)
if ($containers.Count -ne 1 -or $containers[0].name -ne $containerName) {
    throw "Expected a single $containerName container in $appName. Review capacity manually for multi-container apps."
}
if ($app.properties.runningStatus -ne 'Running' -or $app.properties.provisioningState -ne 'Succeeded') {
    throw 'Start the app and wait for its current deployment to finish before resizing.'
}
if ($app.properties.configuration.activeRevisionsMode -ne 'Single') {
    throw 'This workflow requires single-revision mode to avoid changing an unexpected traffic target.'
}
$container = $containers[0]
$before = "$($container.resources.cpu) vCPU / $($container.resources.memory)"
if ([string]$container.resources.cpu -eq $Cpu -and $container.resources.memory -eq $memory) {
    Write-CapacitySummary "$appName already uses $Cpu vCPU / $memory per replica; no change."
    return
}
$suffix = "capacity-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
if (-not $env:GITHUB_RUN_ID -or -not $env:GITHUB_RUN_ATTEMPT) {
    throw 'Run this script through the capacity workflow so the revision name is unique.'
}
az containerapp update --resource-group $ResourceGroup --name $appName --container-name $containerName `
    --cpu $Cpu --memory $memory --revision-suffix $suffix --output none
$expectedRevision = "$appName--$suffix"
$deadline = [DateTimeOffset]::UtcNow.AddMinutes(15)
do {
    $current = az containerapp show --resource-group $ResourceGroup --name $appName -o json | ConvertFrom-Json
    if ($current.properties.provisioningState -in @('Failed', 'Canceled')) {
        throw "Capacity update failed for $appName. Inspect revision $expectedRevision."
    }
    if ($current.properties.latestRevisionName -eq $expectedRevision -and
        $current.properties.latestReadyRevisionName -eq $expectedRevision -and
        $current.properties.provisioningState -eq 'Succeeded') {
        $updated = @($current.properties.template.containers | Where-Object { $_.name -eq $containerName })[0]
        if ([string]$updated.resources.cpu -ne $Cpu -or $updated.resources.memory -ne $memory -or $updated.image -ne $container.image) {
            throw 'The resulting image or capacity did not match the requested update.'
        }
        Write-CapacitySummary "$appName : $before → $Cpu vCPU / $memory per replica. Revision $expectedRevision is ready. Image and replica limits were preserved."
        return
    }
    Start-Sleep -Seconds 10
} while ([DateTimeOffset]::UtcNow -lt $deadline)
throw "Timed out waiting for $expectedRevision. Inspect Container App revision health and logs."
