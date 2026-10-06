import { useState, type FormEvent } from 'react'
import { RotateCcw, Save, ShieldCheck } from 'lucide-react'
import { saveSitePermission, type SitePermissionInput, type SitePermissionResult } from '../api/client'
import { ErrorBanner, LoadingBar } from '../components/ui'

const initial: SitePermissionInput = {
  tenantId: '', clientId: '', clientSecret: '', siteUrl: '', targetClientId: '', targetDisplayName: '', role: 'read',
}

export default function SitePermissionsPage() {
  const [input, setInput] = useState(initial)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<SitePermissionResult | null>(null)

  const save = async (event: FormEvent) => {
    event.preventDefault()
    if (busy) {
      return
    }
    setBusy(true)
    setError(null)
    setResult(null)
    const request = { ...input }
    setInput(current => ({ ...current, clientSecret: '' }))
    try {
      setResult(await saveSitePermission(request))
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not configure site permissions.')
    } finally {
      request.clientSecret = ''
      setBusy(false)
    }
  }

  return <section className="card">
    <div className="card-head"><h2><ShieldCheck size={18} />SharePoint site access</h2></div>
    <div className="card-body site-access-body">
      <p>Grant or update an application's read/write access to a SharePoint site.</p>
      <details className="site-access-help">
        <summary>Required permissions and credential handling</summary>
        <p>The privileged application needs Microsoft Graph <code>Sites.FullControl.All</code>; the target needs <code>Sites.Selected</code>.
          Both require admin consent in the same tenant. This form changes site access only.</p>
        <p>The secret is used for this request only, is never saved, and clears on submission or when leaving this tab.</p>
      </details>
      <LoadingBar active={busy} />
      {error && <ErrorBanner message={error} />}
      <form className="site-access-form" onSubmit={event => void save(event)} autoComplete="off">
        <fieldset className="site-access-group" disabled={busy}>
          <legend>Privileged application</legend>
          <label className="site-access-field">Tenant ID
            <input required type="text" value={input.tenantId} onChange={event => setInput({ ...input, tenantId: event.target.value.trim() })} placeholder="Directory (tenant) ID" />
          </label>
          <label className="site-access-field">Privileged client ID
            <input required type="text" value={input.clientId} onChange={event => setInput({ ...input, clientId: event.target.value.trim() })} placeholder="Application (client) ID" />
          </label>
          <label className="site-access-field">Privileged client secret
            <input required type="password" autoComplete="new-password" maxLength={4096} value={input.clientSecret}
              onChange={event => setInput({ ...input, clientSecret: event.target.value })} placeholder="Secret value, not secret ID" />
          </label>
        </fieldset>
        <fieldset className="site-access-group" disabled={busy}>
          <legend>Target application and site</legend>
          <label className="site-access-field">Site URL
            <input required type="url" value={input.siteUrl} onChange={event => setInput({ ...input, siteUrl: event.target.value.trim() })} placeholder="https://contoso.sharepoint.com/sites/team" />
          </label>
          <label className="site-access-field">Target client ID
            <input required type="text" value={input.targetClientId} onChange={event => setInput({ ...input, targetClientId: event.target.value.trim() })} placeholder="Application receiving site access" />
          </label>
          <label className="site-access-field">Target application name
            <input required type="text" maxLength={256} value={input.targetDisplayName} onChange={event => setInput({ ...input, targetDisplayName: event.target.value })} />
          </label>
          <label className="site-access-field">Permission
            <select value={input.role} onChange={event => setInput({ ...input, role: event.target.value as 'read' | 'write' })}>
              <option value="read">Read</option>
              <option value="write">Write (includes read)</option>
            </select>
          </label>
        </fieldset>
        <div className="row site-access-actions">
          <button type="submit" className="primary" disabled={busy}><Save size={15} aria-hidden="true" />{busy ? 'Saving site permission…' : 'Save site permission'}</button>
          <button type="button" className="site-access-clear" disabled={busy} onClick={() => {
            setInput(initial)
            setError(null)
            setResult(null)
          }}><RotateCcw size={15} aria-hidden="true" />Clear form</button>
        </div>
      </form>
      {result && <div className="site-access-result" role="status">
        <strong>Site permission {result.updated ? 'updated' : 'granted'}.</strong>
        <p>Application <code>{result.targetClientId}</code> has <strong>{result.role}</strong> access on site <code>{result.siteId}</code>.</p>
      </div>}
    </div>
  </section>
}
