param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'test')]
    [string] $EnvironmentName,

    [Parameter(Mandatory)]
    [ValidateSet('start', 'stop')]
    [string] $Action,

    [Parameter(Mandatory)]
    [ValidateSet('Background', 'MarkItDown', 'PageIndex', 'Ollaya')]
    [string] $Component,

    [string] $ResourceGroup
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$parametersFile = Join-Path $PSScriptRoot "../../infra/parameters.$EnvironmentName.json"
$settings = (Get-Content -LiteralPath $parametersFile -Raw | ConvertFrom-Json).parameters
if ($settings.environmentName.value -ne $EnvironmentName) {
    throw 'Parameter environment does not match the selected environment.'
}
if ([string]::IsNullOrWhiteSpace($ResourceGroup)) {
    $ResourceGroup = "rg-$($settings.workloadName.value)-$EnvironmentName"
}

$outputName = switch ($Component) {
    'Background' {
        'workerContainerAppName'
    }
    'MarkItDown' {
        'markItDownContainerAppName'
    }
    'PageIndex' {
        'pageIndexContainerAppName'
    }
    'Ollaya' {
        'ollayaContainerAppName'
    }
}
# Ollaya has its own template and environment (infra/Ollaya); the other apps come from infra/ContainerApps.
$deploymentName = if ($Component -eq 'Ollaya') {
    "ollaya-$EnvironmentName"
} else {
    "container-apps-$EnvironmentName"
}
$containerName = $Component.ToLowerInvariant()

$appName = az deployment group show --resource-group $ResourceGroup `
    --name $deploymentName --query "properties.outputs.$outputName.value" --output tsv
if ([string]::IsNullOrWhiteSpace($appName)) {
    throw "$Component Container App output is missing from deployment $deploymentName. Run its infrastructure workflow first."
}
$appId = az containerapp show --resource-group $ResourceGroup --name $appName --query id --output tsv
if ([string]::IsNullOrWhiteSpace($appId)) {
    throw "Could not resolve $Component Container App $appName."
}

$appUrl = "https://management.azure.com$appId"
$apiVersion = '2025-01-01'
$desiredStatus = if ($Action -eq 'start') {
    'Running'
} else {
    'Stopped'
}
$deadline = [DateTimeOffset]::UtcNow.AddMinutes(15)
$requested = $false

while ($true) {
    $app = az rest --method get --url "$appUrl`?api-version=$apiVersion" --output json | ConvertFrom-Json
    if (@($app.properties.template.containers | Where-Object { $_.name -eq $containerName }).Count -ne 1) {
        throw "Expected a $containerName container in $appName. Check the infrastructure deployment output."
    }
    $status = $app.properties.runningStatus
    Write-Host "$Component $appName : runningStatus=$status"
    if ($status -eq $desiredStatus) {
        break
    }
    if ($app.properties.provisioningState -in @('Failed', 'Canceled')) {
        throw "Container App $appName provisioning state is $($app.properties.provisioningState). Check Azure before retrying."
    }
    if ($status -notin @('Running', 'Stopped', 'Progressing')) {
        throw "Unexpected running status '$status' for $appName."
    }
    if ([DateTimeOffset]::UtcNow -ge $deadline) {
        throw "Timed out waiting for $appName to become $desiredStatus. Last status: $status. Check the Azure activity log."
    }
    # Let any existing transition finish before submitting an opposite action.
    if (-not $requested -and $status -ne 'Progressing') {
        Write-Host "Requesting $Action for $appName in $ResourceGroup."
        az rest --method post --url "$appUrl/$Action`?api-version=$apiVersion" --output none
        $requested = $true
    }
    Start-Sleep -Seconds 10
}

Write-Host "$Component $appName is $desiredStatus."
if ($env:GITHUB_STEP_SUMMARY) {
    @"
## $Component Container App
- Environment: $EnvironmentName
- Resource group: $ResourceGroup
- Container App: $appName
- Requested action: $Action
- Running status: $desiredStatus

Running status confirms the Azure app state; check service health and container logs to verify application readiness.
"@ | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
}
