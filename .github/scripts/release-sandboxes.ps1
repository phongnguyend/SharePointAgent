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
if ($outputs.sandboxGroupName.value) {
  "- Sandbox group: $($outputs.sandboxGroupName.value)" >> $env:GITHUB_STEP_SUMMARY
  "Register the image as a disk image in the sandbox group (portal: Disk images, Create, Base Image URL = the image above, Registry Authentication = managed identity $($outputs.sandboxesPullIdentityId.value)). New sandboxes then boot from it." >> $env:GITHUB_STEP_SUMMARY
} else {
  "No sandbox group is deployed. Set deploySandboxGroup to true in the environment parameter file and run Deploy infrastructure to create one." >> $env:GITHUB_STEP_SUMMARY
}
