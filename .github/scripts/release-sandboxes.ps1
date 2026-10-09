$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
. "$PSScriptRoot/sandbox-host-image.ps1"

$outputs = az deployment group show --resource-group $env:RESOURCE_GROUP --name "infra-$env:DEPLOY_ENVIRONMENT" --query properties.outputs -o json | ConvertFrom-Json
if (-not $outputs.containerRegistryName.value) {
  throw 'Run Deploy infrastructure before releasing Sandboxes.'
}

# Sandboxes boot from a disk image made from this image; nothing long-running is updated here. The
# image is smoke-tested in a local container before it is pushed.
$image = Publish-SandboxHostImage $outputs

"## Sandboxes image published" >> $env:GITHUB_STEP_SUMMARY
"- Image: $image" >> $env:GITHUB_STEP_SUMMARY
. "$PSScriptRoot/optional-deployments.ps1"
$sandboxGroupName = Get-SandboxGroupName
if ($sandboxGroupName) {
  $pullIdentityId = Get-OptionalDeploymentOutput 'sandboxes' 'sandboxesPullIdentityId'
  "- Sandbox group: $sandboxGroupName" >> $env:GITHUB_STEP_SUMMARY
  "Register the image as a disk image in the sandbox group (portal: Disk images, Create, Base Image URL = the image above, Registry Authentication = managed identity $pullIdentityId). New sandboxes then boot from it. Set SANDBOX_DISK_IMAGE_ID to its resource ID and run Release API." >> $env:GITHUB_STEP_SUMMARY
} else {
  "No sandbox group is deployed. Run Deploy Sandboxes Infrastructure to create one." >> $env:GITHUB_STEP_SUMMARY
}
