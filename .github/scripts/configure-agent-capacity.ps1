param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'test')]
    [string] $EnvironmentName,

    [Parameter(Mandatory)]
    [ValidateSet('0.5', '1', '2')]
    [string] $Cpu,

    [string] $ResourceGroup
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$memory = @{ '0.5' = '1Gi'; '1' = '2Gi'; '2' = '4Gi' }[$Cpu]
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
        "## AgentHost capacity ($EnvironmentName)`n$Details" | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
    }
}

$projectEndpoint = $outputs.hosting.value.foundryProjectEndpoint
if ([string]::IsNullOrWhiteSpace($projectEndpoint)) {
    throw 'Foundry project output is missing. Run Deploy infrastructure first.'
}
function Invoke-CapacityFoundry([string] $Method, [string] $Path, $Body = $null, [string] $ContentType = 'application/json') {
    $token = az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv
    $arguments = @{
        Method = $Method
        Uri = "$($projectEndpoint.TrimEnd('/'))/$($Path)?api-version=v1"
        Headers = @{ Authorization = "Bearer $token" }
        ContentType = $ContentType
        TimeoutSec = 120
    }
    if ($null -ne $Body) {
        $arguments.Body = $Body | ConvertTo-Json -Depth 50 -Compress
    }
    # Avoid retrying version creation, which could create duplicate versions.
    return Invoke-RestMethod @arguments
}
$agent = Invoke-CapacityFoundry 'GET' 'agents/sharepoint-agent'
$rules = @($agent.agent_endpoint.version_selector.version_selection_rules)
if ($rules.Count -ne 1 -or $rules[0].type -ne 'FixedRatio' -or $rules[0].traffic_percentage -ne 100 -or -not $rules[0].agent_version) {
    throw 'Expected one explicitly routed AgentHost version receiving 100% of traffic. Configure routing before resizing.'
}
$previousVersion = [string]$rules[0].agent_version
$previous = Invoke-CapacityFoundry 'GET' "agents/sharepoint-agent/versions/$previousVersion"
if ($previous.status -ne 'active' -or $previous.definition.kind -ne 'hosted' -or -not $previous.definition.container_configuration.image) {
    throw 'The routed version must be an active, container-image hosted agent.'
}
$definition = $previous.definition
$before = "$($definition.cpu) vCPU / $($definition.memory)"
if ([string]$definition.cpu -eq $Cpu -and $definition.memory -eq $memory) {
    Write-CapacitySummary "AgentHost version $previousVersion already uses $Cpu vCPU / $memory per session; no change."
    return
}
$definition.cpu = $Cpu
$definition.memory = $memory
$created = Invoke-CapacityFoundry 'POST' 'agents/sharepoint-agent/versions' @{ definition = $definition }
$version = [string]$created.version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'Foundry did not return a new version. Routing was not changed.'
}
Write-CapacitySummary "Created AgentHost version $version from $previousVersion : $before → $Cpu vCPU / $memory per session. Waiting for activation; routing is unchanged."
$deadline = [DateTimeOffset]::UtcNow.AddMinutes(15)
do {
    $state = Invoke-CapacityFoundry 'GET' "agents/sharepoint-agent/versions/$version"
    if ($state.status -in @('failed', 'deleted', 'deleting')) {
        throw "Foundry version $version entered $($state.status). Previous routing is unchanged."
    }
    if ($state.status -eq 'active') {
        break
    }
    Start-Sleep -Seconds 10
} while ([DateTimeOffset]::UtcNow -lt $deadline)
if ($state.status -ne 'active') {
    throw "Timed out waiting for Foundry version $version. Previous routing is unchanged."
}
# Check for manual routing changes outside the GitHub deployment lock.
$current = Invoke-CapacityFoundry 'GET' 'agents/sharepoint-agent'
$currentRules = @($current.agent_endpoint.version_selector.version_selection_rules)
if ($currentRules.Count -ne 1 -or $currentRules[0].agent_version -ne $previousVersion -or $currentRules[0].traffic_percentage -ne 100) {
    throw 'Agent routing changed while resizing. The new version is ready but was not routed.'
}
$null = Invoke-CapacityFoundry 'PATCH' 'agents/sharepoint-agent' @{
    agent_endpoint = @{
        version_selector = @{
            version_selection_rules = @(@{ type = 'FixedRatio'; agent_version = $version; traffic_percentage = 100 })
        }
    }
} 'application/merge-patch+json'
$verified = Invoke-CapacityFoundry 'GET' 'agents/sharepoint-agent'
$verifiedRules = @($verified.agent_endpoint.version_selector.version_selection_rules)
if ($verifiedRules.Count -ne 1 -or $verifiedRules[0].agent_version -ne $version -or $verifiedRules[0].traffic_percentage -ne 100) {
    throw "Could not confirm routing to version $version. Inspect Foundry routing before retrying."
}
Write-CapacitySummary "AgentHost now routes 100% to version $version ($Cpu vCPU / $memory per session). Previous version: $previousVersion. Existing image, environment, and protocols were reused."
