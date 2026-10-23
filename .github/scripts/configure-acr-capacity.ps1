param(
    [Parameter(Mandatory)]
    [ValidateSet('local', 'dev', 'test')]
    [string] $EnvironmentName,

    [Parameter(Mandatory)]
    [ValidateSet('Basic', 'Standard', 'Premium')]
    [string] $Sku,

    [Parameter(Mandatory)]
    [ValidateRange(0, 2)]
    [int] $PoolCount,

    [string] $ResourceGroup
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
if ($Sku -ne 'Premium' -and $PoolCount -gt 0) {
    throw 'Dedicated build agents require Premium. Choose pool count 0 for Basic or Standard.'
}
$settings = (Get-Content -LiteralPath (Join-Path $PSScriptRoot "../../infra/parameters.$EnvironmentName.json") -Raw | ConvertFrom-Json).parameters
if ($settings.environmentName.value -ne $EnvironmentName) {
    throw 'Parameter environment does not match the selected environment.'
}
if ([string]::IsNullOrWhiteSpace($ResourceGroup)) {
    $ResourceGroup = "rg-$($settings.workloadName.value)-$EnvironmentName"
}
$name = az deployment group show --resource-group $ResourceGroup --name "infra-$EnvironmentName" `
    --query properties.outputs.containerRegistryName.value -o tsv
if ([string]::IsNullOrWhiteSpace($name)) {
    throw 'Container Registry output is missing. Run Deploy Infrastructure first.'
}
$before = az acr show --resource-group $ResourceGroup --name $name --query sku.name -o tsv
$poolName = 'nativebuild'
$pools = @(az acr agentpool list --registry $name --resource-group $ResourceGroup -o json | ConvertFrom-Json)
$pool = $pools | Where-Object { $_.name -eq $poolName }
$runs = @(az acr task list-runs --registry $name --resource-group $ResourceGroup `
    --query "[?status=='Running' || status=='Queued' || status=='Started']" -o json | ConvertFrom-Json)
if ($runs.Count -gt 0) {
    throw 'Wait for active or queued ACR builds to finish before changing registry or pool capacity.'
}
if ($Sku -ne 'Premium') {
    if (@($pools | Where-Object { $_.name -ne $poolName }).Count -gt 0) {
        throw 'Other agent pools exist. Review them before downgrading; this workflow manages only nativebuild.'
    }
    if ($pool) {
        az acr agentpool delete --registry $name --resource-group $ResourceGroup --name $poolName --yes
    }
} elseif ($pool -and $pool.tier -ne 'S3') {
    throw 'Existing nativebuild pool is not S3. Review it manually before resizing.'
}
if ($before -ne $Sku) {
    # Azure validates other Premium dependencies; only our named build pool is managed here.
    az acr update --resource-group $ResourceGroup --name $name --sku $Sku --output none
}
if ($Sku -eq 'Premium') {
    if ($pool) {
        az acr agentpool update --registry $name --resource-group $ResourceGroup --name $poolName --count $PoolCount --output none
    } elseif ($PoolCount -gt 0) {
        az acr agentpool create --registry $name --resource-group $ResourceGroup --name $poolName --tier S3 --count $PoolCount --output none
    }
    if ($pool -or $PoolCount -gt 0) {
        $verifiedPool = az acr agentpool show --registry $name --resource-group $ResourceGroup --name $poolName -o json | ConvertFrom-Json
        if ($verifiedPool.count -ne $PoolCount -or $verifiedPool.provisioningState -ne 'Succeeded') {
            throw 'Build pool did not reach the requested capacity.'
        }
    }
}
$after = az acr show --resource-group $ResourceGroup --name $name --query sku.name -o tsv
if ($after -ne $Sku) {
    throw "Registry tier verification failed: expected $Sku, found $after."
}
$summary = @"
## Container Registry capacity ($EnvironmentName)

Registry: $name

Tier: $before -> $after

Build pool: nativebuild, S3 (8 vCPU / 16 GiB per agent), requested agents: $PoolCount.
Basic/Standard removes nativebuild; Premium with count 0 pauses an existing pool.
Shared infrastructure redeployment applies the tier configured in infra/main.bicep.
"@
Write-Host $summary
if ($env:GITHUB_STEP_SUMMARY) {
    $summary | Add-Content -LiteralPath $env:GITHUB_STEP_SUMMARY
}
