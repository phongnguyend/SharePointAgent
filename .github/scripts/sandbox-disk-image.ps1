# Data-plane contract: azure-containerapps-sandbox 0.1.0b4, ImageOperationsMixin.create_disk_image.
function Publish-SandboxDiskImage([string]$Image, [string]$Group, [string]$Region, [string]$PullIdentityId) {
  if ([string]::IsNullOrWhiteSpace($Image) -or [string]::IsNullOrWhiteSpace($Group) -or
      [string]::IsNullOrWhiteSpace($PullIdentityId) -or $Region -notmatch '^[a-z0-9]+$') {
    throw 'Image, sandbox group, region, and pull identity are required to register a disk image.'
  }
  $subscriptionId = az account show --query id -o tsv
  if ($LASTEXITCODE -ne 0) {
    throw 'Could not determine the Azure subscription.'
  }
  $endpoint = "https://management.$Region.azuredevcompute.io"
  $groupPath = "/subscriptions/$subscriptionId/resourceGroups/$([Uri]::EscapeDataString($env:RESOURCE_GROUP))/sandboxGroups/$([Uri]::EscapeDataString($Group))"
  $collectionUrl = "$endpoint$groupPath/diskimages"
  $apiVersion = '2026-02-01-preview'
  $requestPath = Join-Path $env:RUNNER_TEMP 'sandbox-disk-image.request.json'
  $outputPath = Join-Path $env:RUNNER_TEMP 'sandbox-disk-image.outputs.json'
  try {
    @{
      image = @{ base = $Image }
      labels = @{ name = "sandboxhost-$env:IMAGE_TAG" }
      managedIdentityResourceId = $PullIdentityId
    } | ConvertTo-Json -Depth 5 | Set-Content $requestPath -Encoding utf8
    $disk = az rest --method put --url "$collectionUrl`?api-version=$apiVersion" --resource https://dynamicsessions.io --body "@$requestPath" -o json | ConvertFrom-Json
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($disk.id)) {
      throw 'Disk image registration did not return an ID.'
    }
    $diskId = [string]$disk.id
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes(20)
    while ($disk.status.state -notin @('Ready', 'Succeeded')) {
      if ($disk.status.state -in @('Failed', 'Canceled', 'Cancelled')) {
        throw "Disk image $diskId failed: $($disk.status.message)"
      }
      if ([DateTimeOffset]::UtcNow -ge $deadline) {
        throw "Timed out waiting for disk image $diskId to become ready."
      }
      Write-Host "Disk image $diskId state: $($disk.status.state). Waiting..."
      Start-Sleep -Seconds 10
      $disk = az rest --method get --url "$collectionUrl/$([Uri]::EscapeDataString($diskId))?api-version=$apiVersion" --resource https://dynamicsessions.io -o json | ConvertFrom-Json
      if ($LASTEXITCODE -ne 0 -or [string]$disk.id -ne $diskId) {
        throw "Could not read disk image $diskId."
      }
    }
    # Publish only a ready image. Failed builds leave the last successful release output intact.
    @{
      '$schema' = 'https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#'
      contentVersion = '1.0.0.0'
      resources = @()
      outputs = @{
        sandboxDiskImageId = @{ type = 'string'; value = $diskId }
        sandboxContainerImage = @{ type = 'string'; value = $Image }
        sandboxGroupName = @{ type = 'string'; value = $Group }
      }
    } | ConvertTo-Json -Depth 8 | Set-Content $outputPath -Encoding utf8
    az deployment group create --resource-group $env:RESOURCE_GROUP --name "sandbox-image-$env:DEPLOY_ENVIRONMENT" `
      --mode Incremental --template-file $outputPath --output none | Out-Host
    if ($LASTEXITCODE -ne 0) {
      throw 'Disk image is ready, but saving its release output failed.'
    }
    return $diskId
  } finally {
    foreach ($path in @($requestPath, $outputPath)) {
      if (Test-Path -LiteralPath $path) {
        Remove-Item -LiteralPath $path -Force
      }
    }
  }
}
