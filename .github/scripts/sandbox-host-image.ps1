# Shared by the Dynamic Sessions and Sandboxes releases, which deploy the same SharePointAgent.SandboxHost
# image. One smoke test covers it, whether it runs on the runner or behind a session pool.

function Get-SandboxUrl($BaseUrl, $Path, $Identifier) {
  $url = "$($BaseUrl.TrimEnd('/'))$Path"
  if ([string]::IsNullOrWhiteSpace($Identifier)) {
    return $url
  }
  $separator = '?'
  if ($url.Contains('?')) {
    $separator = '&'
  }
  return "$url$separator" + "identifier=$([Uri]::EscapeDataString($Identifier))"
}

# A proxy can return JSON with a non-JSON content type, leaving Invoke-RestMethod's result as text.
function ConvertFrom-SandboxResponse($Response) {
  if ($Response -is [string]) {
    try {
      return ($Response | ConvertFrom-Json -ErrorAction Stop)
    } catch {
      $preview = $Response.Substring(0, [Math]::Min(512, $Response.Length))
      throw "Sandbox host returned non-JSON content: $preview"
    }
  }
  return $Response
}

function Wait-SandboxHealth($BaseUrl, $Headers, $Identifier, $Attempts = 30) {
  $lastFailure = 'No health response was received.'
  for ($attempt = 1; $attempt -le $Attempts; $attempt++) {
    try {
      $response = Invoke-RestMethod (Get-SandboxUrl $BaseUrl '/health' $Identifier) -Headers $Headers -TimeoutSec 30
      $health = ConvertFrom-SandboxResponse $response
      if ($health.status -eq 'ok') {
        return
      }
      $preview = ConvertTo-Json -InputObject $health -Depth 5 -Compress
      $lastFailure = "Expected health status 'ok'. Response: $($preview.Substring(0, [Math]::Min(512, $preview.Length)))"
    } catch {
      $lastFailure = $_.Exception.Message
    }
    if ($attempt -lt $Attempts) {
      Start-Sleep -Seconds 5
    }
  }
  throw "The sandbox host at $BaseUrl did not report healthy after $Attempts attempts. $lastFailure"
}

# Runs one script per language and a file round trip, failing on anything but the expected output.
function Test-SandboxHost($BaseUrl, $Headers, $Identifier, $HealthAttempts = 30) {
  Wait-SandboxHealth $BaseUrl $Headers $Identifier $HealthAttempts
  $cases = @(
    @{ language = 'python'; code = 'print(6 * 7)' }
    @{ language = 'node'; code = 'console.log(6 * 7)' }
    @{ language = 'powershell'; code = 'Write-Output (6 * 7)' }
    @{ language = 'bash'; code = 'echo $((6 * 7))' }
  )
  foreach ($case in $cases) {
    $result = Invoke-RestMethod -Method Post (Get-SandboxUrl $BaseUrl '/executions' $Identifier) -Headers $Headers `
      -ContentType 'application/json' -Body ($case | ConvertTo-Json -Compress) -TimeoutSec 120
    $result = ConvertFrom-SandboxResponse $result
    if ($result.exitCode -ne 0 -or $result.stdout.Trim() -ne '42') {
      throw "$($case.language) smoke test failed: exit $($result.exitCode), stdout '$($result.stdout)', stderr '$($result.stderr)'."
    }
    Write-Host "$($case.language) ran in $($result.durationMs) ms."
  }
  $body = @{ path = 'release-smoke/check.txt'; content = 'release smoke test' } | ConvertTo-Json -Compress
  Invoke-RestMethod -Method Put (Get-SandboxUrl $BaseUrl '/files/text' $Identifier) -Headers $Headers -ContentType 'application/json' -Body $body -TimeoutSec 60 | Out-Null
  $read = Invoke-RestMethod (Get-SandboxUrl $BaseUrl '/files/text?path=release-smoke/check.txt' $Identifier) -Headers $Headers -TimeoutSec 60
  $read = ConvertFrom-SandboxResponse $read
  if ($read.content -ne 'release smoke test') {
    throw 'File round trip returned different content.'
  }
}

# Builds the image on the runner, smoke-tests it in a local container, and only then pushes it. In a
# combined release both components share IMAGE_TAG, so the second finds the image already pushed.
function Publish-SandboxHostImage($Outputs) {
  $repository = 'sandboxhost'
  $remote = "$($Outputs.containerRegistryLoginServer.value)/$repository`:$env:IMAGE_TAG"
  # The repository does not exist before the first release, which makes this lookup fail; treat that as "not pushed".
  $PSNativeCommandUseErrorActionPreference = $false
  $existing = az acr repository show-tags --name $Outputs.containerRegistryName.value --repository $repository --query "[?@=='$env:IMAGE_TAG']" -o tsv 2>$null
  $PSNativeCommandUseErrorActionPreference = $true
  if ($LASTEXITCODE -eq 0 -and $existing -eq $env:IMAGE_TAG) {
    Write-Host "$remote was already built and tested in this run."
    return $remote
  }
  $local = "$repository`:smoke"
  # Native output goes to the host so it is not mixed into the returned image reference.
  docker build --file 'backend/SharePointAgent.SandboxHost/Dockerfile' --tag $local . | Out-Host
  $key = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
  $container = "$repository-smoke-$env:GITHUB_RUN_ID"
  docker run --detach --name $container --publish 18080:8080 --env "Sandbox__ApiKey=$key" $local | Out-Null
  try {
    Test-SandboxHost 'http://localhost:18080' @{ 'X-Api-Key' = $key } $null
  } catch {
    docker logs $container | Out-Host
    throw
  } finally {
    docker rm --force $container | Out-Null
  }
  az acr login --name $Outputs.containerRegistryName.value --output none | Out-Host
  docker tag $local $remote | Out-Host
  docker push $remote | Out-Host
  return $remote
}
