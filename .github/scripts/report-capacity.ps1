param(
    [Parameter(Mandatory)]
    [ValidateSet('local', 'dev', 'test')]
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
. "$PSScriptRoot/deployment-outputs.ps1"
# Select only resource names and the project URL; deployment outputs can contain credentials.
function Get-AppNames($Deployment) {
    $PSNativeCommandUseErrorActionPreference = $false
    $names = az deployment group show --resource-group $ResourceGroup --name "$Deployment-$EnvironmentName" `
        --query 'properties.outputs.{Api:apiContainerAppName.value,Background:workerContainerAppName.value,MarkItDown:markItDownContainerAppName.value,PageIndex:pageIndexContainerAppName.value}' -o json 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $names) {
        return $null
    }
    $names = $names | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($names.Api)) {
        return $null
    }
    return $names
}
# Before the first Deploy Container Apps infrastructure run, the apps are still named by the infra
# deployment that created them.
$apps = Get-AppNames 'container-apps'
if (-not $apps) {
    $apps = Get-AppNames 'infra'
}
if (-not $apps) {
    $apps = [pscustomobject]@{}
}
$PSNativeCommandUseErrorActionPreference = $false
$project = az deployment group show --resource-group $ResourceGroup --name "infra-$EnvironmentName" `
    --query 'properties.outputs.hosting.value.foundryProjectEndpoint' -o tsv 2>$null
if ($LASTEXITCODE -ne 0) {
    $project = ''
}
$PSNativeCommandUseErrorActionPreference = $true
# System One services, Dynamic Sessions, and Sandboxes have optional templates, so a missing
# deployment is reported as not deployed rather than as an error.
$ollaya = Get-OptionalDeploymentOutput 'ollaya' 'ollayaContainerAppName' $ResourceGroup $EnvironmentName
$winnowNative = Get-OptionalDeploymentOutput 'winnownative' 'winnownativeContainerAppName' $ResourceGroup $EnvironmentName
$layaNative = Get-OptionalDeploymentOutput 'layanative' 'layanativeContainerAppName' $ResourceGroup $EnvironmentName
$outputs = [pscustomobject]@{
    Api = $apps.Api
    Background = $apps.Background
    MarkItDown = $apps.MarkItDown
    PageIndex = $apps.PageIndex
    Ollaya = $ollaya
    'Winnow Native' = $winnowNative
    'Laya Native' = $layaNative
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
$lines.Add('## Azure Container Registry')
$lines.Add('')
$lines.Add('| Registry | Region | Tier | Provisioning state |')
$lines.Add('| --- | --- | --- | --- |')
$registryName = $null
try {
    $registryName = az deployment group show --resource-group $ResourceGroup --name "infra-$EnvironmentName" `
        --query properties.outputs.containerRegistryName.value -o tsv
    if ([string]::IsNullOrWhiteSpace($registryName)) {
        throw 'Registry deployment output is missing.'
    }
    $registry = az acr show --resource-group $ResourceGroup --name $registryName `
        --query '{name:name,location:location,tier:sku.name,state:provisioningState}' -o json | ConvertFrom-Json
    $lines.Add("| $(Cell $registry.name) | $(Cell $registry.location) | $(Cell $registry.tier) | $(Cell $registry.state) |")
} catch {
    $errors.Add('Container Registry capacity could not be read. Check shared infrastructure outputs and Azure read permissions.')
    $lines.Add('| Read failed | - | - | - |')
}
$lines.Add('')
$lines.Add('## ACR build-agent pools')
$lines.Add('')
$lines.Add('Dedicated pool configuration, not live CPU utilization. Zero instances means paused; allocated instances are billed even when idle. The default shared ACR builder is not a dedicated pool.')
$lines.Add('')
$lines.Add('| Pool | Tier | Provisioning state | Capacity state | Instances | vCPU per instance | Memory GB per instance |')
$lines.Add('| --- | --- | --- | --- | --- | --- | --- |')
try {
    if ([string]::IsNullOrWhiteSpace($registryName)) {
        throw 'Registry deployment output is missing.'
    }
    $pools = @(az acr agentpool list --resource-group $ResourceGroup --registry $registryName `
        --query '[].{name:name,tier:tier,state:provisioningState,count:count}' -o json | ConvertFrom-Json)
    $poolCapacity = @{
        S1 = @{ Cpu = 2; Memory = 3 }
        S2 = @{ Cpu = 4; Memory = 8 }
        S3 = @{ Cpu = 8; Memory = 16 }
        I6 = @{ Cpu = 64; Memory = 216 }
    }
    if ($pools.Count -eq 0) {
        $lines.Add('| No dedicated pools | - | - | - | 0 | - | - |')
    }
    foreach ($pool in $pools) {
        $capacity = $poolCapacity[$pool.tier]
        $capacityState = 'Allocated'
        if ($null -eq $pool.count) {
            $capacityState = 'Unknown'
        } elseif ($pool.count -eq 0) {
            $capacityState = 'Paused'
        }
        $lines.Add("| $(Cell $pool.name) | $(Cell $pool.tier) | $(Cell $pool.state) | $capacityState | $(Cell $pool.count) | $(Cell $capacity.Cpu) | $(Cell $capacity.Memory) |")
    }
} catch {
    $errors.Add('ACR build-agent pools could not be read. Check registry availability and Azure agent-pool read permissions.')
    $lines.Add('| Read failed | - | - | - | - | - | - |')
}
$lines.Add('')
$lines.Add('## Container Apps')
$lines.Add('')
$lines.Add('Active revision configuration is shown below. CPU and memory are per container per replica; replica limits are per revision, not current replica counts. A stopped app retains its configuration.')
$lines.Add('')
$lines.Add('| Service | App / state | Active revision | Container | vCPU | Memory | Min replicas | Max replicas |')
$lines.Add('| --- | --- | --- | --- | --- | --- | --- | --- |')
foreach ($component in @('Api', 'Background', 'MarkItDown', 'PageIndex', 'Ollaya', 'Winnow Native', 'Laya Native')) {
    try {
        $name = $outputs.$component
        if ($component -in @('Ollaya', 'Winnow Native', 'Laya Native') -and [string]::IsNullOrWhiteSpace($name)) {
            $lines.Add("| $component | Not deployed | - | - | - | - | - | - |")
            continue
        }
        if ([string]::IsNullOrWhiteSpace($name)) {
            $errors.Add("$component is not in the container-apps-$EnvironmentName or infra-$EnvironmentName deployment outputs. Run Deploy Container Apps infrastructure.")
            $lines.Add("| $component | Not found | - | - | - | - | - | - |")
            continue
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
$lines.Add('## Dynamic Sessions')
$lines.Add('')
$lines.Add('Session pool configuration. CPU and memory are per session; ready sessions are kept warm and billed while waiting.')
$lines.Add('')
$lines.Add('| Pool / state | vCPU per session | Memory per session | Ready sessions | Max sessions | Cooldown (s) | Egress |')
$lines.Add('| --- | --- | --- | --- | --- | --- | --- |')
try {
    $poolName = Get-OptionalDeploymentOutput 'dynamic-sessions' 'dynamicSessionsPoolName' $ResourceGroup $EnvironmentName
    if ([string]::IsNullOrWhiteSpace($poolName)) {
        $lines.Add('| Not deployed | - | - | - | - | - | - |')
    } else {
        $poolId = az resource show --resource-group $ResourceGroup --name $poolName --resource-type Microsoft.App/sessionPools --query id -o tsv
        $pool = az rest --method get --url "https://management.azure.com$($poolId)?api-version=2026-07-01" `
            --query 'properties.{state:provisioningState,resources:customContainerTemplate.containers[0].resources,scale:scaleConfiguration,cooldown:dynamicPoolConfiguration.lifecycleConfiguration.cooldownPeriodInSeconds,egress:sessionNetworkConfiguration.status}' -o json | ConvertFrom-Json
        $lines.Add("| $(Cell $poolName) / $(Cell $pool.state) | $(Cell $pool.resources.cpu) | $(Cell $pool.resources.memory) | $(Cell $pool.scale.readySessionInstances) | $(Cell $pool.scale.maxConcurrentSessions) | $(Cell $pool.cooldown) | $(Cell $pool.egress) |")
    }
} catch {
    $errors.Add('Dynamic Sessions capacity could not be read. Check the dynamic-sessions deployment outputs and Azure read permissions.')
    $lines.Add('| Read failed | - | - | - | - | - | - |')
}
$lines.Add('')
$lines.Add('## Sandboxes')
$lines.Add('')
$lines.Add('The API sizes each sandbox when it creates one, so CPU, memory, and auto-suspend come from its settings (application defaults where unset). Live sandboxes are not counted.')
$lines.Add('')
$lines.Add('| Sandbox group / state | Region | API workspace mode | CPU per sandbox | Memory per sandbox | Auto-suspend (s) |')
$lines.Add('| --- | --- | --- | --- | --- | --- |')
try {
    $groupName = Get-OptionalDeploymentOutput 'sandboxes' 'sandboxGroupName' $ResourceGroup $EnvironmentName
    if ([string]::IsNullOrWhiteSpace($groupName)) {
        $lines.Add('| Not deployed | - | - | - | - | - |')
    } else {
        $group = az resource show --resource-group $ResourceGroup --name $groupName --resource-type Microsoft.App/sandboxGroups `
            --api-version 2026-02-01-preview --query '{state:properties.provisioningState,location:location}' -o json | ConvertFrom-Json
        # Select only agent workspace settings; other API environment variables can hold values that do not belong in the report.
        $apiSettings = @{}
        if (-not [string]::IsNullOrWhiteSpace($outputs.Api)) {
            $variables = @(az containerapp show --resource-group $ResourceGroup --name $outputs.Api `
                --query "properties.template.containers[?name=='api'].env[] | [?starts_with(name, 'AgentWorkspace__')].{name:name,value:value}" -o json | ConvertFrom-Json)
            foreach ($variable in $variables) {
                $apiSettings[$variable.name] = $variable.value
            }
        }
        $mode = $apiSettings['AgentWorkspace__DefaultMode']
        $cpu = $apiSettings['AgentWorkspace__Sandboxes__Cpu']
        $memory = $apiSettings['AgentWorkspace__Sandboxes__Memory']
        $suspend = $apiSettings['AgentWorkspace__Sandboxes__AutoSuspendSeconds']
        if ([string]::IsNullOrWhiteSpace($cpu)) {
            $cpu = '1000m (default)'
        }
        if ([string]::IsNullOrWhiteSpace($memory)) {
            $memory = '2048Mi (default)'
        }
        if ([string]::IsNullOrWhiteSpace($suspend)) {
            $suspend = '900 (default)'
        }
        $lines.Add("| $(Cell $groupName) / $(Cell $group.state) | $(Cell $group.location) | $(Cell $mode) | $(Cell $cpu) | $(Cell $memory) | $(Cell $suspend) |")
    }
} catch {
    $errors.Add('Sandboxes capacity could not be read. Check the sandboxes deployment outputs and Azure read permissions.')
    $lines.Add('| Read failed | - | - | - | - | - |')
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
