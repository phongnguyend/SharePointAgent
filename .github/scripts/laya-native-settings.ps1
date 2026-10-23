# Deployment settings for LayaNative.
function Get-LayaNativeSettings {
    param(
        [ValidateSet('dev', 'test')]
        [string] $EnvironmentName
    )

    $Component = 'LayaNative'
    $id = 'layanative'
    $parametersFile = "infra/SystemOne/$Component/parameters.$EnvironmentName.json"
    $parameters = (Get-Content -LiteralPath $parametersFile -Raw | ConvertFrom-Json).parameters
    $shared = (Get-Content "infra/parameters.$EnvironmentName.json" -Raw | ConvertFrom-Json).parameters
    if ($parameters.environmentName.value -ne $EnvironmentName -or $shared.environmentName.value -ne $EnvironmentName) {
        throw 'Parameter environment does not match the selected environment.'
    }
    if ($parameters.workloadName.value -ne $shared.workloadName.value) {
        throw "workloadName in infra/SystemOne/$Component must match infra/parameters."
    }
    $resourceGroup = $env:AZURE_RESOURCE_GROUP
    if ([string]::IsNullOrWhiteSpace($resourceGroup)) {
        $resourceGroup = "rg-$($shared.workloadName.value)-$EnvironmentName"
    }
    $port = 8000
    $keyName = 'LAYA_API_KEY'
    $model = 'english'
    return @{
        Id = $id
        Component = $Component
        ResourceGroup = $resourceGroup
        ParametersFile = $parametersFile
        Deployment = "$id-$EnvironmentName"
        Port = $port
        KeyName = $keyName
        SecretName = "$id-api-key"
        Model = $model
    }
}

function Assert-LayaNativeKey {
    if ([string]::IsNullOrWhiteSpace($env:LAYA_NATIVE_API_KEY) -or $env:LAYA_NATIVE_API_KEY.Trim().Length -lt 32 -or $env:LAYA_NATIVE_API_KEY -match '[\r\n]') {
        throw 'Set the service API-key secret to a single-line token of at least 32 characters; ingress is external.'
    }
}
