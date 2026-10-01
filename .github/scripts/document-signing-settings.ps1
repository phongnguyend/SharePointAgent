function Get-DocumentSigningSettings {
  $values = @{}
  $secretValues = @()
  $secretReferences = @()
  foreach ($provider in @('DocuSign', 'AdobeSign')) {
    $prefix = "DocumentSigning__${provider}__"
    $enabledKey = "${prefix}Enabled"
    $raw = [Environment]::GetEnvironmentVariable($enabledKey.ToUpperInvariant())
    $enabled = $false
    if (-not [string]::IsNullOrWhiteSpace($raw) -and -not [bool]::TryParse($raw, [ref]$enabled)) {
      throw "Set $($enabledKey.ToUpperInvariant()) to true or false."
    }
    $values[$enabledKey] = $enabled.ToString().ToLowerInvariant()
    $oauthRedirect = [Environment]::GetEnvironmentVariable('DOCUMENTSIGNING__ADOBESIGN__OAUTHREDIRECTURI')
    $oauthSetup = $provider -eq 'AdobeSign' -and -not [string]::IsNullOrWhiteSpace($oauthRedirect)
    if ($provider -eq 'AdobeSign') {
      $authUrl = [Environment]::GetEnvironmentVariable('DOCUMENTSIGNING__ADOBESIGN__AUTHURL')
      if (($enabled -or $oauthSetup) -and [string]::IsNullOrWhiteSpace($authUrl)) {
        throw 'Set DOCUMENTSIGNING__ADOBESIGN__AUTHURL in the selected GitHub environment.'
      }
      $values['DocumentSigning__AdobeSign__AuthUrl'] = [string]$authUrl
      $accessTokenUrl = [Environment]::GetEnvironmentVariable('DOCUMENTSIGNING__ADOBESIGN__ACCESSTOKENURL')
      if ($oauthSetup -and [string]::IsNullOrWhiteSpace($accessTokenUrl)) {
        throw 'Set DOCUMENTSIGNING__ADOBESIGN__ACCESSTOKENURL in the selected GitHub environment for OAuth setup.'
      }
      $values['DocumentSigning__AdobeSign__AccessTokenUrl'] = [string]$accessTokenUrl
      $values['DocumentSigning__AdobeSign__ApiAccessPoint'] = [string][Environment]::GetEnvironmentVariable('DOCUMENTSIGNING__ADOBESIGN__APIACCESSPOINT')
      $values['DocumentSigning__AdobeSign__OAuthRedirectUri'] = [string]$oauthRedirect
    }
    if (-not $enabled -and -not $oauthSetup) {
      continue
    }
    $keys = @('ApiAccessPoint', 'ClientId', 'ClientSecret', 'RefreshToken')
    if ($oauthSetup) {
      $redirect = $null
      if (-not [Uri]::TryCreate($oauthRedirect, [UriKind]::Absolute, [ref]$redirect) -or $redirect.Scheme -ne 'https' -or $redirect.Query -or $redirect.Fragment -or $redirect.UserInfo) {
        throw 'Set DOCUMENTSIGNING__ADOBESIGN__OAUTHREDIRECTURI to an absolute HTTPS callback URL without a query or fragment.'
      }
      $values['DocumentSigning__AdobeSign__OAuthRedirectUri'] = $oauthRedirect
      if (-not $enabled) {
        $keys = @('ClientId', 'ClientSecret')
      }
    }
    if ($provider -eq 'DocuSign') {
      $keys = @('Demo', 'ApiBaseUrl', 'AccountId', 'ClientId', 'SenderUserId', 'PrivateKeyPem')
      $returnUrl = [Environment]::GetEnvironmentVariable('DOCUMENTSIGNING__RETURNURL')
      if ([string]::IsNullOrWhiteSpace($returnUrl)) {
        throw 'Set DOCUMENTSIGNING__RETURNURL in the selected GitHub environment.'
      }
      $values['DocumentSigning__ReturnUrl'] = $returnUrl
    }
    foreach ($key in $keys) {
      $runtimeKey = "$prefix$key"
      $githubKey = $runtimeKey.ToUpperInvariant()
      $value = [Environment]::GetEnvironmentVariable($githubKey)
      if ($key -eq 'Demo' -and [string]::IsNullOrWhiteSpace($value)) {
        $value = 'true'
      }
      if ([string]::IsNullOrWhiteSpace($value)) {
        throw "Set $githubKey in the selected GitHub environment."
      }
      if ($key -eq 'Demo') {
        $demo = $false
        if (-not [bool]::TryParse($value, [ref]$demo)) {
          throw "Set $githubKey to true or false."
        }
        $value = $demo.ToString().ToLowerInvariant()
      }
      if ($key -in @('PrivateKeyPem', 'ClientSecret', 'RefreshToken')) {
        $secretName = "signing-$provider-$key".ToLowerInvariant()
        $secretValues += @{ name = $secretName; value = $value }
        $secretReferences += @{ name = $runtimeKey; secretRef = $secretName }
      } else {
        $values[$runtimeKey] = $value
      }
    }
  }
  return @{
    Environment = @($values.GetEnumerator() | ForEach-Object { @{ name = $_.Key; value = [string]$_.Value } }) + $secretReferences
    Secrets = $secretValues
  }
}
