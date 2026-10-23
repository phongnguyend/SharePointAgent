param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'test')]
    [string] $EnvironmentName
)

$Component = 'LayaNative'
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
. "$PSScriptRoot/laya-native-settings.ps1"
$settings = Get-LayaNativeSettings $EnvironmentName
$id = $settings.Id
$group = $settings.ResourceGroup
$shared = az deployment group show --resource-group $group --name "infra-$EnvironmentName" --query properties.outputs -o json | ConvertFrom-Json
if (-not $shared.containerRegistryName.value -or -not $shared.logAnalyticsWorkspaceName.value) {
    throw 'Run Deploy infrastructure first to publish the registry and Log Analytics workspace outputs.'
}

# Preserve a released image on infrastructure reruns unless an explicit override is supplied.
$image = $env:LAYA_NATIVE_IMAGE
if ([string]::IsNullOrWhiteSpace($image)) {
    $deployments = @(az deployment group list --resource-group $group --query '[].name' -o tsv)
    if ($settings.Deployment -in $deployments) {
        $outputs = az deployment group show --resource-group $group --name $settings.Deployment --query properties.outputs -o json | ConvertFrom-Json
        $name = $outputs."${id}ContainerAppName".value
        if ($name) {
            $image = az containerapp show --resource-group $group --name $name --query "properties.template.containers[?name=='$id'].image | [0]" -o tsv
        }
    }
}
$parameters = @{
    containerRegistryName = @{ value = $shared.containerRegistryName.value }
    logAnalyticsWorkspaceName = @{ value = $shared.logAnalyticsWorkspaceName.value }
}
if (-not [string]::IsNullOrWhiteSpace($image) -and $image -ne 'mcr.microsoft.com/k8se/quickstart:latest') {
    Assert-LayaNativeKey
    $parameters["${id}Image"] = @{ value = $image.Trim() }
    $parameters["${id}ApiKey"] = @{ value = $env:LAYA_NATIVE_API_KEY }
}
$parameterPath = Join-Path $env:RUNNER_TEMP "$id.parameters.json"
$templatePath = Join-Path $env:RUNNER_TEMP "$id.json"
try {
    New-Item -ItemType File -Path $parameterPath -Force | Out-Null
    chmod 600 $parameterPath
    @{ parameters = $parameters } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $parameterPath
    az bicep build --file "infra/SystemOne/$Component/main.bicep" --outfile $templatePath
    az deployment group validate --resource-group $group --template-file $templatePath --parameters $settings.ParametersFile $parameterPath --output none
    $outputs = az deployment group create --resource-group $group --name $settings.Deployment --mode Incremental `
        --template-file $templatePath --parameters $settings.ParametersFile $parameterPath --query properties.outputs -o json | ConvertFrom-Json
    "## $Component infrastructure deployed" >> $env:GITHUB_STEP_SUMMARY
    "- Container App: $($outputs."${id}ContainerAppName".value)" >> $env:GITHUB_STEP_SUMMARY
    "- Endpoint: $($outputs."${id}Endpoint".value)" >> $env:GITHUB_STEP_SUMMARY
    if (-not $parameters.ContainsKey("${id}Image")) {
        'Running the bootstrap hello image. Run the service release workflow to install inference.' >> $env:GITHUB_STEP_SUMMARY
    }
} finally {
    foreach ($path in @($parameterPath, $templatePath)) {
        if (Test-Path -LiteralPath $path) {
            Remove-Item -LiteralPath $path -Force
        }
    }
}
