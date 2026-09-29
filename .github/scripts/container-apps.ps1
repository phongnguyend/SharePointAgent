function Write-RequestBody($Name, $Body) {
  $path = "$env:RUNNER_TEMP/$Name.request.json"
  New-Item -ItemType File -Path $path -Force | Out-Null
  chmod 600 $path
  $Body | ConvertTo-Json -Depth 50 | Set-Content $path
  return $path
}
function Update-ContainerApp($Name, $ContainerName, $Environment, $Secrets, $Port = 0) {
  $app = az containerapp show --resource-group $env:RESOURCE_GROUP --name $Name -o json | ConvertFrom-Json -AsHashtable
  $url = "https://management.azure.com$($app.id)"
  $containers = @($app.properties.template.containers)
  $container = @($containers | Where-Object { $_.name -eq $ContainerName })
  if ($container.Count -ne 1) {
    throw "Expected container $ContainerName in $Name. Run the infrastructure workflow first."
  }
  $container = $container[0]
  $container.image = "$($outputs.containerRegistryLoginServer.value)/$ContainerName`:$env:IMAGE_TAG"
  $container.env = @($container.env | Where-Object { $_.name -notin $Environment.name }) + $Environment
  if ($Port -gt 0) {
    # Replace hello-image probes, including any automatically added TCP probes.
    $container.probes = @(@{ type = 'Readiness'; httpGet = @{ path = '/health'; port = $Port }; periodSeconds = 10 })
  }
  $existingSecrets = az rest --method post --url "$url/listSecrets?api-version=2025-01-01" -o json | ConvertFrom-Json -AsHashtable
  $configuration = @{
    secrets = @($existingSecrets.value | Where-Object { $_.name -notin $Secrets.name }) + $Secrets
  }
  if ($Port -gt 0) {
    $configuration.ingress = @{ targetPort = $Port }
  }
  # Merge Patch preserves infrastructure settings; arrays must retain unrelated entries.
  $body = @{ properties = @{
    configuration = $configuration
    template = @{ containers = $containers; revisionSuffix = "release-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT" }
  } }
  $path = Write-RequestBody $ContainerName $body
  try {
    az rest --method patch --url "$url`?api-version=2025-01-01" --body "@$path" --output none
  } finally {
    Remove-Item -LiteralPath $path -Force
  }
}

function Wait-ContainerApp($Name) {
  $ready = $false
  $expectedRevision = "$Name--release-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
  for ($attempt = 0; $attempt -lt 90; $attempt++) {
    $app = az containerapp show --resource-group $env:RESOURCE_GROUP --name $name -o json | ConvertFrom-Json
    if ($app.properties.provisioningState -in @('Failed', 'Canceled')) {
      throw "Container App $name entered state $($app.properties.provisioningState)."
    }
    if ($app.properties.provisioningState -eq 'Succeeded' -and
        $app.properties.latestRevisionName -eq $expectedRevision -and
        $app.properties.latestReadyRevisionName -eq $app.properties.latestRevisionName) {
      $ready = $true
      break
    }
    if ($attempt % 6 -eq 0 -and $app.properties.latestRevisionName -eq $expectedRevision) {
      $revision = az containerapp revision show --resource-group $env:RESOURCE_GROUP --name $Name --revision $expectedRevision -o json | ConvertFrom-Json
      Write-Host "Waiting for $expectedRevision : provisioning=$($revision.properties.provisioningState), running=$($revision.properties.runningState), health=$($revision.properties.healthState)"
      if ($revision.properties.provisioningState -eq 'Failed' -or $revision.properties.runningState -eq 'Failed') {
        throw "Revision $expectedRevision failed: $($revision.properties.provisioningError). Check its container console logs."
      }
    }
    Start-Sleep -Seconds 10
  }
  if (-not $ready) {
    throw "Timed out waiting for $expectedRevision. Resource provisioning does not confirm application readiness. Check container console logs: az containerapp logs show --resource-group $env:RESOURCE_GROUP --name $Name --revision $expectedRevision --type console --tail 100"
  }
}
