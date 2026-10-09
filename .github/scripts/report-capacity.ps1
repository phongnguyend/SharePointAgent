param(
    [Parameter(Mandatory)]
    [ValidateSet('dev', 'test')]
    [string] $EnvironmentName,

    [string] $ResourceGroup
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$settings = (Get-Content -LiteralPath (Join-Path $PSScriptRoot "../../infra/parameters.$EnvironmentName.json") -Raw | ConvertFrom-Json).parameters
if ($settings.environmentName.value -ne $EnvironmentName) {
    throw 'Parameter environment does not match the selected environment.'
}
if ([string]::IsNullOrWhiteSpace($ResourceGroup)) {
    $ResourceGroup = "rg-$($settings.workloadName.value)-$EnvironmentName"
}
# Select only resource names and the project URL; deployment outputs can contain credentials.
$apps = az deployment group show --resource-group $ResourceGroup --name "container-apps-$EnvironmentName" `
    --query 'properties.outputs.{Api:apiContainerAppName.value,Background:workerContainerAppName.value,MarkItDown:markItDownContainerAppName.value,PageIndex:pageIndexContainerAppName.value}' -o json | ConvertFrom-Json
$project = az deployment group show --resource-group $ResourceGroup --name "infra-$EnvironmentName" `
    --query 'properties.outputs.hosting.value.foundryProjectEndpoint' -o tsv
$outputs = [pscustomobject]@{
    Api = $apps.Api
    Background = $apps.Background
    MarkItDown = $apps.MarkItDown
    PageIndex = $apps.PageIndex
    project = $project
}
$lines = [Collections.Generic.List[string]]::new()
$errors = [Collections.Generic.List[string]]::new()
function Cell($Value) {
    if ($null -eq $Value -or [string]$Value -eq '') {
        return 'N/A'
    }
    return ([string]$Value).Replace('|', '\|').Replace("`r", ' ').Replace("`n", ' ')
}
$lines.Add("# Current capacity ($EnvironmentName)")
$lines.Add('')
$lines.Add("Resource group: $(Cell $ResourceGroup). Checked at $([DateTimeOffset]::UtcNow.ToString('u')).")
$lines.Add('')
$lines.Add('## Container Apps')
$lines.Add('')
$lines.Add('Active revision configuration is shown below. CPU and memory are per container per replica; replica limits are per revision, not current replica counts. A stopped app retains its configuration.')
$lines.Add('')
$lines.Add('| Service | App / state | Active revision | Container | vCPU | Memory | Min replicas | Max replicas |')
$lines.Add('| --- | --- | --- | --- | --- | --- | --- | --- |')
foreach ($component in @('Api', 'Background', 'MarkItDown', 'PageIndex')) {
    try {
        $name = $outputs.$component
        if ([string]::IsNullOrWhiteSpace($name)) {
            throw 'Infrastructure output is missing.'
        }
        $app = az containerapp show --resource-group $ResourceGroup --name $name `
            --query '{state:properties.runningStatus}' -o json | ConvertFrom-Json
        $revisions = @(az containerapp revision list --resource-group $ResourceGroup --name $name `
            --query '[?properties.active].{name:name,containers:properties.template.containers[].{name:name,cpu:resources.cpu,memory:resources.memory},scale:properties.template.scale}' -o json | ConvertFrom-Json)
        if ($revisions.Count -eq 0) {
            $lines.Add("| $component | $(Cell $name) / $(Cell $app.state) | No active revisions | - | - | - | - | - |")
        }
        foreach ($revision in $revisions) {
            foreach ($container in $revision.containers) {
                $lines.Add("| $component | $(Cell $name) / $(Cell $app.state) | $(Cell $revision.name) | $(Cell $container.name) | $(Cell $container.cpu) | $(Cell $container.memory) | $(Cell $revision.scale.minReplicas) | $(Cell $revision.scale.maxReplicas) |")
            }
        }
    } catch {
        # Keep upstream response bodies out of the published report.
        $errors.Add("$component capacity could not be read. Check infrastructure outputs and Azure read permissions.")
        $lines.Add("| $component | Read failed | - | - | - | - | - | - |")
    }
}
$lines.Add('')
$lines.Add('## AgentHost')
$lines.Add('')
$lines.Add('Capacity is per session for each explicitly routed version. This check does not invoke the agent or start a session.')
$lines.Add('')
$lines.Add('| Routed version | Status | Traffic (%) | vCPU per session | Memory per session |')
$lines.Add('| --- | --- | --- | --- | --- |')
try {
    if ([string]::IsNullOrWhiteSpace($outputs.project)) {
        throw 'Foundry project output is missing.'
    }
    $token = az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv
    $baseUrl = "$($outputs.project.TrimEnd('/'))/agents/sharepoint-agent"
    $headers = @{ Authorization = "Bearer $token" }
    $agent = Invoke-RestMethod -Method Get -Uri "$($baseUrl)?api-version=v1" -Headers $headers -TimeoutSec 60
    $rules = @($agent.agent_endpoint.version_selector.version_selection_rules)
    if ($rules.Count -eq 0) {
        throw 'No explicit routed version was found.'
    }
    foreach ($rule in $rules) {
        if ([string]::IsNullOrWhiteSpace($rule.agent_version)) {
            throw 'A routing rule has no explicit version.'
        }
        $version = [Uri]::EscapeDataString([string]$rule.agent_version)
        $state = Invoke-RestMethod -Method Get -Uri "$baseUrl/versions/$($version)?api-version=v1" -Headers $headers -TimeoutSec 60
        $lines.Add("| $(Cell $rule.agent_version) | $(Cell $state.status) | $(Cell $rule.traffic_percentage) | $(Cell $state.definition.cpu) | $(Cell $state.definition.memory) |")
    }
} catch {
    $errors.Add('AgentHost capacity could not be read. Check that the agent is deployed, explicitly routed, and the deployment identity has Foundry read access.')
    $lines.Add('| Read failed | - | - | - | - |')
}
if ($errors.Count -gt 0) {
    $lines.Add('')
    $lines.Add('## Incomplete report')
    foreach ($message in $errors) {
        $lines.Add("- $message")
    }
}
$report = $lines -join "`n"
Write-Host $report
if ($env:GITHUB_STEP_SUMMARY) {
    $report | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
}
if ($errors.Count -gt 0) {
    throw 'Capacity report is incomplete. See the job summary for affected services.'
}
