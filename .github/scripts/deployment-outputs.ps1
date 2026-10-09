# Outputs of the infrastructure deployments. infra/main.bicep deploys as infra-<environment>; Container
# Apps, Ollaya, Dynamic Sessions, and Sandboxes have their own templates and deployments.
function Get-OptionalDeploymentOutput($Deployment, $Output, $ResourceGroup = $env:RESOURCE_GROUP, $EnvironmentName = $env:DEPLOY_ENVIRONMENT) {
  $PSNativeCommandUseErrorActionPreference = $false
  $value = az deployment group show --resource-group $ResourceGroup --name "$Deployment-$EnvironmentName" `
    --query "properties.outputs.$Output.value" -o tsv 2>$null
  if ($LASTEXITCODE -ne 0) {
    return ''
  }
  return [string]$value
}
function Get-OptionalDeploymentOutputs($Deployment, $ResourceGroup, $EnvironmentName) {
  $PSNativeCommandUseErrorActionPreference = $false
  $json = az deployment group show --resource-group $ResourceGroup --name "$Deployment-$EnvironmentName" `
    --query properties.outputs -o json 2>$null
  if ($LASTEXITCODE -ne 0 -or -not $json) {
    return $null
  }
  return $json | ConvertFrom-Json
}
# The shared outputs with the Container Apps outputs merged in, so callers read app names and URLs the
# same way as the rest. Returns $null before Deploy infrastructure has run.
function Get-InfrastructureOutputs($ResourceGroup = $env:RESOURCE_GROUP, $EnvironmentName = $env:DEPLOY_ENVIRONMENT) {
  $outputs = Get-OptionalDeploymentOutputs 'infra' $ResourceGroup $EnvironmentName
  if (-not $outputs) {
    return $null
  }
  $containerApps = Get-OptionalDeploymentOutputs 'container-apps' $ResourceGroup $EnvironmentName
  if ($containerApps) {
    foreach ($property in $containerApps.PSObject.Properties) {
      $outputs | Add-Member -NotePropertyName $property.Name -NotePropertyValue $property.Value -Force
    }
  }
  return $outputs
}
function Get-OllayaEndpoint {
  return Get-OptionalDeploymentOutput 'ollaya' 'ollayaEndpoint'
}
function Get-DynamicSessionsPoolEndpoint {
  return Get-OptionalDeploymentOutput 'dynamic-sessions' 'dynamicSessionsPoolEndpoint'
}
function Get-SandboxGroupName {
  return Get-OptionalDeploymentOutput 'sandboxes' 'sandboxGroupName'
}
