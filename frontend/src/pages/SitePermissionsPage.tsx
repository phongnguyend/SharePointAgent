import { useEffect, useState, type FormEvent } from 'react'
import { Eye, List, Pencil, RotateCcw, Save, ShieldCheck, Trash2 } from 'lucide-react'
import { deleteSitePermission, getSitePermissionDefaults, listSitePermissions, saveSitePermission, type SitePermissionInput, type SitePermissionResult, type SitePermissionListing } from '../api/client'
import { ErrorBanner, LoadingBar, Modal } from '../components/ui'

const initial: SitePermissionInput = {
  tenantId: '', clientId: '', clientSecret: '', siteUrl: '', targetClientId: '', targetDisplayName: '', role: 'read',
}

export default function SitePermissionsPage() {
  const [input, setInput] = useState(initial)
  const [defaults, setDefaults] = useState(initial)
  const [loadingDefaults, setLoadingDefaults] = useState(true)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [result, setResult] = useState<SitePermissionResult | null>(null)
  const [listing, setListing] = useState<(SitePermissionListing & { siteUrl: string; tenantId: string }) | null>(null)
  const [deleting, setDeleting] = useState<(SitePermissionListing['permissions'][number] & { siteUrl: string; tenantId: string; privilegedClientId: string }) | null>(null)
  const [deleteSecret, setDeleteSecret] = useState('')
  const [deleteError, setDeleteError] = useState<string | null>(null)
  const [deletedMessage, setDeletedMessage] = useState<string | null>(null)

  useEffect(() => {
    const controller = new AbortController()
    void getSitePermissionDefaults(controller.signal).then(values => {
      if (!controller.signal.aborted) {
        const populated = { ...initial, ...values }
        setDefaults(populated)
        setInput(populated)
      }
    }).catch(() => {
      if (!controller.signal.aborted) {
        setError('Could not load SharePoint defaults. Enter the values manually.')
      }
    }).finally(() => {
      if (!controller.signal.aborted) {
        setLoadingDefaults(false)
      }
    })
    return () => controller.abort()
  }, [])

  const closeDelete = () => {
    if (!busy) {
      setDeleting(null)
      setDeleteSecret('')
      setDeleteError(null)
    }
  }

  const remove = async (event: FormEvent) => {
    event.preventDefault()
    if (busy || !deleting || !deleteSecret) {
      return
    }
    const selected = deleting
    const request = { tenantId: selected.tenantId, clientId: selected.privilegedClientId, clientSecret: deleteSecret,
      siteUrl: selected.siteUrl, targetClientId: selected.clientId, permissionId: selected.permissionId }
    setBusy(true)
    setDeleteError(null)
    setDeleteSecret('')
    setResult(null)
    try {
      await deleteSitePermission(request)
      setListing(current => current && current.siteUrl === selected.siteUrl && current.tenantId === selected.tenantId
        ? { ...current, permissions: current.permissions.filter(grant => grant.permissionId !== selected.permissionId) } : current)
      setDeletedMessage(`Deleted permission for ${selected.displayName || selected.clientId} on ${selected.siteUrl}.`)
      setDeleting(null)
    } catch (cause) {
      setDeleteError(cause instanceof Error ? cause.message : 'Could not delete permission. Refresh the list before retrying.')
    } finally {
      request.clientSecret = ''
      setBusy(false)
    }
  }

  const view = async () => {
    if (busy) {
      return
    }
    setBusy(true)
    setError(null)
    setResult(null)
    setListing(null)
    setDeletedMessage(null)
    const request = { tenantId: input.tenantId, clientId: input.clientId, clientSecret: input.clientSecret, siteUrl: input.siteUrl }
    setInput(current => ({ ...current, clientSecret: '' }))
    try {
      setListing({ ...await listSitePermissions(request), siteUrl: request.siteUrl, tenantId: request.tenantId })
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not load site permissions.')
    } finally {
      request.clientSecret = ''
      setBusy(false)
    }
  }

  const save = async (event: FormEvent) => {
    event.preventDefault()
    if (busy) {
      return
    }
    setBusy(true)
    setError(null)
    setResult(null)
    setListing(null)
    setDeletedMessage(null)
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
      <p>View current application permissions or grant an application read/write access to a SharePoint site.</p>
      <details className="site-access-help">
        <summary>Required permissions and credential handling</summary>
        <p>The privileged application needs Microsoft Graph <code>Sites.FullControl.All</code>; the target needs <code>Sites.Selected</code>.
          Both require admin consent in the same tenant. This form changes site access only.</p>
        <p>The secret is used for this request only, is never saved, and clears on submission or when leaving this tab.</p>
      </details>
      <LoadingBar active={busy || loadingDefaults} />
      {error && <ErrorBanner message={error} />}
      <form className="site-access-form" onSubmit={event => void save(event)} autoComplete="off">
        <fieldset className="site-access-group" disabled={busy || loadingDefaults}>
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
        <fieldset className="site-access-group" disabled={busy || loadingDefaults}>
          <legend>Target application and site</legend>
          <label className="site-access-field">Site URL
            <input required type="url" value={input.siteUrl} onChange={event => setInput({ ...input, siteUrl: event.target.value.trim() })} placeholder="https://contoso.sharepoint.com/sites/team" />
          </label>
          <label className="site-access-field">Target client ID
            <input required type="text" value={input.targetClientId} onChange={event => setInput({ ...input, targetClientId: event.target.value.trim() })} placeholder="Application receiving site access" />
          </label>
          <label className="site-access-field">Target application name (optional)
            <input type="text" maxLength={256} value={input.targetDisplayName} onChange={event => setInput({ ...input, targetDisplayName: event.target.value })} placeholder="Display label for the grant" />
            <small>The client ID identifies the application. Leave blank to use the client ID as the label.</small>
          </label>
          <fieldset className="site-access-permission">
            <legend>Permission</legend>
            <div className="site-access-permission-options">
              <label className="site-access-permission-card">
                <input type="radio" name="site-permission" value="read" checked={input.role === 'read'}
                  aria-describedby="site-permission-read-description"
                  onChange={() => setInput({ ...input, role: 'read' })} />
                <Eye size={18} aria-hidden="true" />
                <span className="site-access-permission-copy">
                  <strong>Read</strong>
                  <span id="site-permission-read-description">View and download content</span>
                </span>
              </label>
              <label className="site-access-permission-card">
                <input type="radio" name="site-permission" value="write" checked={input.role === 'write'}
                  aria-describedby="site-permission-write-description"
                  onChange={() => setInput({ ...input, role: 'write' })} />
                <Pencil size={18} aria-hidden="true" />
                <span className="site-access-permission-copy">
                  <strong>Write</strong>
                  <span id="site-permission-write-description">Read, create, edit and delete content</span>
                </span>
              </label>
            </div>
          </fieldset>
        </fieldset>
        <div className="row site-access-actions">
          <button type="button" className="site-access-clear" disabled={busy || !input.tenantId || !input.clientId || !input.clientSecret || !input.siteUrl}
            onClick={() => void view()}><List size={15} aria-hidden="true" />View permissions</button>
          <button type="submit" className="primary" disabled={busy || loadingDefaults}><Save size={15} aria-hidden="true" />{busy ? 'Saving site permission…' : 'Save site permission'}</button>
          <button type="button" className="site-access-clear" disabled={busy || loadingDefaults} onClick={() => {
            setInput(defaults)
            setError(null)
            setResult(null)
            setListing(null)
            setDeletedMessage(null)
          }}><RotateCcw size={15} aria-hidden="true" />Clear form</button>
        </div>
      </form>
      {listing && listing.siteUrl === input.siteUrl && listing.tenantId === input.tenantId && <section aria-label="Current site permissions" className="site-access-result">
        <h3>Current application permissions</h3>
        <p>{listing.siteUrl} · Site ID: <code>{listing.siteId}</code></p>
        <p>These are explicit application grants for this site. They do not include user/group access or tenant-wide application permissions.</p>
        {listing.permissions.length === 0 ? <p role="status">No application grants were found for this site.</p> : <div style={{ overflowX: 'auto' }}>
          <table>
            <thead><tr><th>Application</th><th>Client ID</th><th>Permissions</th><th>Permission ID</th><th>Actions</th></tr></thead>
            <tbody>{listing.permissions.map(grant => <tr key={`${grant.permissionId}-${grant.clientId}`}>
              <td>{grant.displayName || 'Unnamed application'}</td><td><code>{grant.clientId}</code></td>
              <td>{grant.roles.join(', ') || 'Not specified'}</td><td><code>{grant.permissionId}</code></td>
              <td><button type="button" className="danger icon-only" disabled={busy || !input.clientId}
                title="Delete permission" aria-label={`Delete permission for ${grant.displayName || grant.clientId}`}
                onClick={() => {
                  setDeleteSecret('')
                  setDeleteError(null)
                  setDeletedMessage(null)
                  setDeleting({ ...grant, siteUrl: listing.siteUrl, tenantId: listing.tenantId, privilegedClientId: input.clientId })
                }}><Trash2 size={15} aria-hidden="true" /></button></td>
            </tr>)}</tbody>
          </table>
        </div>}
      </section>}
      {deletedMessage && <p role="status">{deletedMessage}</p>}
      {deleting && <Modal open title="Delete site permission" icon={<Trash2 size={16} />} onClose={closeDelete}
        footer={<>
          <button type="button" disabled={busy} onClick={closeDelete}>Cancel</button>
          <button type="submit" form="delete-site-permission" className="danger" disabled={busy || !deleteSecret}>
            <Trash2 size={15} aria-hidden="true" />{busy ? 'Deleting…' : 'Delete permission'}
          </button>
        </>}>
        <form id="delete-site-permission" className="stack" onSubmit={event => void remove(event)} autoComplete="off">
          <p>Remove <strong>{deleting.roles.join(', ')}</strong> access for <strong>{deleting.displayName || deleting.clientId}</strong> on {deleting.siteUrl}?</p>
          <p>Client ID: <code>{deleting.clientId}</code></p>
          <p>Other grants or tenant-wide permissions may still provide access.</p>
          {deleteError && <ErrorBanner message={deleteError} />}
          <label className="site-access-field">Privileged client secret
            <input type="password" required autoComplete="new-password" maxLength={4096} disabled={busy}
              value={deleteSecret} onChange={event => setDeleteSecret(event.target.value)} />
          </label>
        </form>
      </Modal>}
      {result && <div className="site-access-result" role="status">
        <strong>Site permission {result.updated ? 'updated' : 'granted'}.</strong>
        <p>Application <code>{result.targetClientId}</code> has <strong>{result.role}</strong> access on site <code>{result.siteId}</code>.</p>
      </div>}
    </div>
  </section>
}
