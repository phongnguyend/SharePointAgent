# Ollaya, Dynamic Sessions and Sandboxes are deployed by their own templates (infra/Ollaya,
# infra/DynamicSessions, infra/Sandboxes), so their outputs come from those deployments, if any.
function Get-OptionalDeploymentOutput($Deployment, $Output) {
  $PSNativeCommandUseErrorActionPreference = $false
  $value = az deployment group show --resource-group $env:RESOURCE_GROUP --name "$Deployment-$env:DEPLOY_ENVIRONMENT" `
    --query "properties.outputs.$Output.value" -o tsv 2>$null
  if ($LASTEXITCODE -ne 0) {
    return ''
  }
  return [string]$value
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
