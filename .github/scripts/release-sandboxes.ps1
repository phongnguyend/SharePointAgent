$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
. "$PSScriptRoot/sandbox-host-image.ps1"

$outputs = az deployment group show --resource-group $env:RESOURCE_GROUP --name "infra-$env:DEPLOY_ENVIRONMENT" --query properties.outputs -o json | ConvertFrom-Json
if (-not $outputs.containerRegistryName.value) {
  throw 'Run Deploy infrastructure before releasing Sandboxes.'
}

. "$PSScriptRoot/deployment-outputs.ps1"
. "$PSScriptRoot/sandbox-disk-image.ps1"
$sandboxGroupName = Get-SandboxGroupName
$pullIdentityId = Get-OptionalDeploymentOutput 'sandboxes' 'sandboxesPullIdentityId'
if (-not $sandboxGroupName -or -not $pullIdentityId) {
  throw 'Run Deploy Sandboxes Infrastructure before releasing Sandboxes.'
}
$region = az resource show --resource-group $env:RESOURCE_GROUP --name $sandboxGroupName `
  --resource-type Microsoft.App/sandboxGroups --query location -o tsv

$image = Publish-SandboxHostImage $outputs
if ($image -isnot [string] -or [string]::IsNullOrWhiteSpace($image)) {
  throw 'Image publishing must return exactly one non-empty image reference string.'
}
$diskId = Publish-SandboxDiskImage -Image $image -Group $sandboxGroupName -Region $region -PullIdentityId $pullIdentityId

"## Sandboxes disk image ready" >> $env:GITHUB_STEP_SUMMARY
"- Image: $image" >> $env:GITHUB_STEP_SUMMARY
"- Sandbox group: $sandboxGroupName" >> $env:GITHUB_STEP_SUMMARY
"- Disk image ID: $diskId" >> $env:GITHUB_STEP_SUMMARY
"Run Release API to use this disk image for new sandboxes. The ID is read automatically from sandbox-image-$env:DEPLOY_ENVIRONMENT. Remove SANDBOX_DISK_IMAGE_ID if it pins an older image; an explicit override takes precedence. Existing sandboxes and older disk images are retained." >> $env:GITHUB_STEP_SUMMARY
