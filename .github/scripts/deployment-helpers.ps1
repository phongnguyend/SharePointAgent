$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
$outputs = Get-Content "$env:RUNNER_TEMP/shared-outputs.json" -Raw | ConvertFrom-Json
$hosting = $outputs.hosting.value
$settings = (Get-Content $env:PARAMETERS_FILE -Raw | ConvertFrom-Json).parameters
. "$PSScriptRoot/container-apps.ps1"
function Invoke-Database($Query, $InputFile) {
  Import-Module SqlServer
  $token = az account get-access-token --resource https://database.windows.net/ --query accessToken -o tsv
  $arguments = @{
    ServerInstance = $hosting.sqlServerFqdn
    Database = $hosting.sqlDatabaseName
    AccessToken = $token
    Encrypt = 'Mandatory'
    AbortOnError = $true
    ConnectionTimeout = 30
    QueryTimeout = 300
    ErrorAction = 'Stop'
  }
  if ($InputFile) {
    $arguments.InputFile = $InputFile
  } else {
    $arguments.Query = $Query
  }
  Invoke-Sqlcmd @arguments | Out-Null
}
function Grant-Role($PrincipalId, $Role, $Scope) {
  az role assignment create --assignee-object-id $PrincipalId --assignee-principal-type ServicePrincipal --role $Role --scope $Scope --output none
}
function Invoke-Foundry($Method, $Path, $Body, $ContentType = 'application/json') {
  for ($attempt = 0; $attempt -lt 30; $attempt++) {
    $token = az account get-access-token --resource https://ai.azure.com --query accessToken -o tsv
    $arguments = @{
      Method = $Method
      Uri = "$($hosting.foundryProjectEndpoint)/$($Path)?api-version=v1"
      Headers = @{ Authorization = "Bearer $token" }
      ContentType = $ContentType
      TimeoutSec = 120
    }
    if ($null -ne $Body) {
      $arguments.Body = $Body | ConvertTo-Json -Depth 30 -Compress
    }
    try {
      return Invoke-RestMethod @arguments
    } catch {
      $status = [int]$_.Exception.Response.StatusCode
      if ($status -notin @(401, 403) -or $attempt -eq 29) {
        throw
      }
      Start-Sleep -Seconds 10
    }
  }
}

function Open-DatabaseFirewall {
  $rule = "github-$env:GITHUB_RUN_ID-$env:GITHUB_RUN_ATTEMPT"
  "SQL_FIREWALL_RULE=$rule" >> $env:GITHUB_ENV
  "SQL_SERVER_NAME=$($hosting.sqlServerName)" >> $env:GITHUB_ENV
  $ip = (Invoke-RestMethod 'https://api.ipify.org').Trim()
  if ([System.Net.IPAddress]::Parse($ip).AddressFamily -ne [System.Net.Sockets.AddressFamily]::InterNetwork) {
    throw 'Expected an IPv4 address for the runner.'
  }
  az sql server firewall-rule create --resource-group $env:RESOURCE_GROUP --server $hosting.sqlServerName `
    --name $rule --start-ip-address $ip --end-ip-address $ip --output none
  for ($attempt = 0; $attempt -lt 30; $attempt++) {
    try {
      Invoke-Database -Query 'SELECT 1'
      break
    } catch {
      if ($_.Exception.ToString() -match 'Login failed for user') {
        throw "SQL rejected the GitHub OIDC deployment identity on $($hosting.sqlServerFqdn)/$($hosting.sqlDatabaseName). Sign in as the SQL Entra administrator and grant that identity database access before retrying. Azure resource roles do not grant SQL database access. See infra/README.md#grant-the-release-identity-sql-access. Original error: $($_.Exception.Message)"
      }
      if ($attempt -eq 29) {
        throw
      }
      Start-Sleep -Seconds 10
    }
  }
}
