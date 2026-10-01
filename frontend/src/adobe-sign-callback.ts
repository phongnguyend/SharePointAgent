const parameters = new URLSearchParams(window.location.search)
window.history.replaceState(null, '', window.location.pathname)
if (window.opener) {
  window.opener.postMessage({
    type: 'adobe-sign-authorization',
    state: parameters.get('state'),
    code: parameters.get('code'),
    apiAccessPoint: parameters.get('api_access_point'),
    error: parameters.has('error') ? parameters.get('error') || 'unknown_error' : null,
    errorDescription: parameters.get('error_description'),
  }, window.location.origin)
  document.getElementById('status')!.textContent = 'Authorization returned. You can close this window.'
} else {
  document.getElementById('status')!.textContent = 'The original application window is unavailable. Return to Document signing and authorize again, keeping that page open.'
}
