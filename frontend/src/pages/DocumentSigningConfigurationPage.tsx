import { useEffect, useRef, useState } from 'react'
import { Copy, ExternalLink, Eye, EyeOff, FilePenLine, RefreshCw, Trash2 } from 'lucide-react'
import { beginAdobeAuthorization, exchangeAdobeAuthorization, getAdobeAuthorizationConfiguration, type AdobeAuthorizationTokens } from '../api/client'
import { useAsync } from '../lib/useAsync'
import { ErrorBanner, LoadingBar } from '../components/ui'

export default function DocumentSigningConfigurationPage() {
  const config = useAsync(getAdobeAuthorizationConfiguration, [])
  const [tokens, setTokens] = useState<AdobeAuthorizationTokens | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [revealed, setRevealed] = useState(false)
  const [copied, setCopied] = useState<string | null>(null)
  const attempt = useRef<{ popup: Window; state: string; deadline: number } | null>(null)
  const controller = useRef<AbortController | null>(null)
  const mounted = useRef(true)
  const callback = config.data?.redirectUri || ''
  let callbackMatches = false
  let callbackIsHttps = false
  try {
    const uri = new URL(callback)
    callbackIsHttps = uri.protocol === 'https:'
    callbackMatches = callbackIsHttps && uri.origin === window.location.origin && uri.pathname === '/adobe-sign-callback.html'
  } catch {
    // Incomplete configuration is shown as setup guidance below.
  }
  const ready = !!config.data?.configured && callbackMatches

  useEffect(() => {
    mounted.current = true
    const receive = (event: MessageEvent) => {
      const active = attempt.current
      if (!active || event.origin !== window.location.origin || event.source !== active.popup || event.data?.type !== 'adobe-sign-authorization') {
        return
      }
      if (event.data.state !== active.state) {
        setError('Authorization did not match this attempt. Please authorize again.')
        return
      }
      attempt.current = null
      active.popup.close()
      if (event.data.error) {
        const providerError = typeof event.data.error === 'string' ? event.data.error.slice(0, 100) : 'unknown_error'
        const description = typeof event.data.errorDescription === 'string' ? event.data.errorDescription.slice(0, 1000) : ''
        const guidance = providerError === 'invalid_scope'
          ? ' Enable all four scopes listed under Configuration and permissions in your Adobe application.'
          : providerError === 'access_denied'
            ? ' Sign in as the shared sender and approve the requested access.'
            : ' Check the Adobe application client ID, registered redirect URI, and enabled scopes.'
        setError(`Adobe authorization failed (${providerError})${description ? `: ${description}` : '.'}${guidance}`)
        setBusy(false)
        return
      }
      if (typeof event.data.code !== 'string' || !event.data.code.trim()) {
        setError('Adobe returned no authorization code. Check that the Adobe application uses the authorization-code flow, then authorize again.')
        setBusy(false)
        return
      }
      const abort = new AbortController()
      controller.current = abort
      void exchangeAdobeAuthorization({ state: active.state, code: event.data.code, apiAccessPoint: typeof event.data.apiAccessPoint === 'string' ? event.data.apiAccessPoint : null }, abort.signal)
        .then(result => {
          if (mounted.current) {
            setTokens(result)
          }
        }).catch(cause => {
          if (mounted.current && !abort.signal.aborted) {
            setError(cause instanceof Error ? cause.message : String(cause))
          }
        }).finally(() => {
          if (mounted.current) {
            setBusy(false)
          }
        })
    }
    window.addEventListener('message', receive)
    const timer = window.setInterval(() => {
      const active = attempt.current
      if (active && (active.popup.closed || Date.now() > active.deadline)) {
        active.popup.close()
        attempt.current = null
        setBusy(false)
        setError('Authorization window closed or timed out. Please authorize again.')
      }
    }, 1000)
    return () => {
      mounted.current = false
      window.removeEventListener('message', receive)
      window.clearInterval(timer)
      controller.current?.abort()
      attempt.current?.popup.close()
      attempt.current = null
    }
  }, [])

  const authorize = async () => {
    if (busy) {
      return
    }
    setError(null)
    setTokens(null)
    setRevealed(false)
    setCopied(null)
    if (!ready) {
      setError('OAuthRedirectUri must point to adobe-sign-callback.html on this frontend origin.')
      return
    }
    const popup = window.open('about:blank', '_blank', 'popup,width=650,height=800')
    if (!popup) {
      setError('Allow popups for this site, then authorize again.')
      return
    }
    setBusy(true)
    try {
      const result = await beginAdobeAuthorization()
      if (!mounted.current) {
        popup.close()
        return
      }
      attempt.current = { popup, state: result.state, deadline: Date.now() + 10 * 60 * 1000 }
      popup.location.href = result.url
    } catch (cause) {
      popup.close()
      if (mounted.current) {
        setError(cause instanceof Error ? cause.message : String(cause))
        setBusy(false)
      }
    }
  }

  const copy = async (label: string, value: string) => {
    try {
      await navigator.clipboard.writeText(value)
      setCopied(label)
    } catch {
      setError('Clipboard access failed. Reveal the values and copy them manually.')
    }
  }

  return <div className="signing-admin">
    <LoadingBar active={config.loading || busy} />
    {(error || config.error) && <ErrorBanner message={error || config.error || ''} />}
    <section className="card">
      <div className="card-head signing-admin-heading">
        <h2><FilePenLine size={18} className="signature-provider-icon adobe-sign" />Adobe Acrobat Sign</h2>
        <span className={`badge ${ready ? 'good' : 'warning'}`}>{config.loading ? 'Checking setup' : ready ? 'Ready to authorize' : 'Setup required'}</span>
        <button className="signing-admin-authorize" disabled={busy || !ready} onClick={() => void authorize()}><ExternalLink size={14} />{busy ? 'Waiting for Adobe…' : 'Authorize with Adobe'}</button>
      </div>
      <div className="card-body signing-admin-body">
        <p>Generate tokens for the shared sender account, then copy them to your configuration. Nothing is saved automatically.</p>
        {config.data && !ready && <div className="signing-setup-notice" role="status">
          <strong>{!callback || !callbackIsHttps ? 'An HTTPS callback is required' : !callbackMatches ? 'Open the matching frontend' : 'Complete the API configuration'}</strong>
          <p>{!callback || !callbackIsHttps
            ? 'Adobe does not accept HTTP callbacks. Use your deployed HTTPS frontend or enable HTTPS locally, then configure its /adobe-sign-callback.html URL.'
            : !callbackMatches ? 'The callback must use this page’s HTTPS origin and end with /adobe-sign-callback.html.'
              : 'Check Adobe ClientId, ClientSecret, AuthUrl, and AccessTokenUrl on the API. Signing can stay disabled while you generate the initial token.'}</p>
        </div>}
        <div className="signing-token-field">
          <label htmlFor="adobe-callback">OAuth callback URL</label>
          <div className="signing-token-value">
            <input id="adobe-callback" type="text" readOnly value={callback} placeholder="https://your-frontend/adobe-sign-callback.html" />
            <button disabled={!callback} onClick={() => void copy('callback URL', callback)} aria-label="Copy callback URL" title="Copy callback URL"><Copy size={14} /></button>
          </div>
          <small>{callbackIsHttps ? 'Register this exact URL in Adobe → API Applications → Configure OAuth.' : 'Set a valid HTTPS callback on the API before registering it in Adobe.'}</small>
        </div>
        <details className="signing-setup-details">
          <summary>Configuration and permissions</summary>
          <p>Current API settings (read-only; use double underscores for environment variables):</p>
          {config.data && <>
            {[
              { key: 'ClientId', value: config.data.clientId },
              { key: 'OAuthRedirectUri', value: config.data.redirectUri },
              { key: 'AuthUrl', value: config.data.authUrl },
              { key: 'AccessTokenUrl', value: config.data.accessTokenUrl },
            ].map(field => <div className="signing-token-field" key={field.key}>
              <label htmlFor={`adobe-setting-${field.key}`}><code>DocumentSigning:AdobeSign:{field.key}</code></label>
              <div className="signing-token-value">
                <input id={`adobe-setting-${field.key}`} type="text" readOnly value={field.value} placeholder="Not configured" spellCheck={false} />
                <button disabled={!field.value} onClick={() => void copy(field.key, field.value)} aria-label={`Copy ${field.key}`} title={`Copy ${field.key}`}><Copy size={14} /></button>
              </div>
            </div>)}
            <div className="signing-token-field">
              <label htmlFor="adobe-setting-secret"><code>DocumentSigning:AdobeSign:ClientSecret</code></label>
              <div className="signing-token-value">
                <input id="adobe-setting-secret" type="text" readOnly value={config.data.clientSecretConfigured ? '•••••••• (configured)' : 'Not configured'} />
              </div>
              <small>The configuration endpoint returns only whether the secret is configured.</small>
            </div>
          </>}
          <p>AccessTokenUrl is required. Token exchange always uses this URL; the callback’s <code>api_access_point</code> does not select the endpoint.</p>
          <p>Enable these Adobe scopes: <code>agreement_read:self agreement_write:self agreement_send:self user_login:self</code>.</p>
          <p>Restart or release the API after updating settings, then refresh setup.</p>
          <button disabled={busy || config.loading} onClick={config.reload}><RefreshCw size={14} />Refresh setup</button>
        </details>
        <p className="signing-admin-note">Authorization opens in a popup. Sign in as the shared organization sender.</p>
      </div>
    </section>
    {tokens && <section className="card">
      <div className="card-head signing-admin-heading"><h2>Token details</h2>
      <div className="row">
        <button onClick={() => setRevealed(value => !value)}>{revealed ? <EyeOff size={14} /> : <Eye size={14} />}{revealed ? 'Hide secrets' : 'Reveal secrets'}</button>
        <button onClick={() => {
          setTokens(null)
          setCopied(null)
          setRevealed(false)
        }}><Trash2 size={14} />Clear tokens</button>
      </div>
      </div>
      <div className="card-body signing-admin-body">
      <p>Copy the refresh token and API origin to your configuration, then release or restart the API.</p>
      {[
        { label: 'Access token', value: tokens.accessToken, secret: true },
        { label: 'Token type', value: tokens.tokenType, secret: false },
        { label: 'DOCUMENTSIGNING__ADOBESIGN__REFRESHTOKEN (GitHub secret)', value: tokens.refreshToken, secret: true },
        { label: 'DOCUMENTSIGNING__ADOBESIGN__APIACCESSPOINT (GitHub variable)', value: tokens.apiAccessPoint, secret: false },
        { label: 'web_access_point', value: tokens.webAccessPoint, secret: false },
        { label: 'expires_in (seconds)', value: tokens.expiresIn?.toString(), secret: false },
        { label: 'access_token_url', value: tokens.accessTokenUrl, secret: false },
        { label: 'client_id', value: tokens.clientId, secret: false },
        { label: 'client_secret', value: tokens.clientSecret, secret: true },
        { label: 'timestamp (Unix milliseconds, received by API)', value: tokens.timestamp.toString(), secret: false },
        { label: 'Received at', value: new Date(tokens.timestamp).toLocaleString(), secret: false },
        { label: 'auth_url', value: tokens.authUrl, secret: false },
        { label: 'redirect_uri', value: tokens.redirectUri, secret: false },
        { label: 'Requested scope', value: tokens.requestedScope, secret: false },
      ].map((field, index) => <div className="signing-token-field" key={field.label}>
        <label htmlFor={`adobe-token-${index}`}>{field.label}</label>
        <div className="signing-token-value">
          <input id={`adobe-token-${index}`} type={field.secret && !revealed ? 'password' : 'text'} readOnly value={field.value ?? ''} placeholder="Not returned by Adobe" autoComplete="off" spellCheck={false} />
          <button disabled={field.value == null} onClick={() => void copy(field.label, field.value ?? '')} aria-label={`Copy ${field.label}`} title={`Copy ${field.label}`}><Copy size={14} /></button>
        </div>
      </div>)}
      <details className="signing-setup-details">
        <summary>Complete Adobe response</summary>
        <p>Includes every field returned by Adobe. Exchange URL, client credentials, and receipt timestamp above are added by this application.</p>
        {revealed ? <textarea aria-label="Complete Adobe response" readOnly rows={12} value={JSON.stringify(tokens.providerResponse, null, 2)} spellCheck={false} />
          : <p>Use Reveal secrets to view the response, including tokens.</p>}
        <button onClick={() => void copy('Adobe response', JSON.stringify(tokens.providerResponse, null, 2))}><Copy size={14} />Copy complete response</button>
      </details>
      <p className="signing-admin-note">Leaving this tab clears tokens and credentials. Copied values remain on your clipboard.</p>
      </div>
    </section>}
    {copied && <p className="signing-copy-notice" role="status">Copied {copied}.</p>}
  </div>
}
