import { useState } from 'react'
import { Users, UserPlus, Pencil, RefreshCw, ShieldCheck, Save, X, LoaderCircle, HardDrive, Cpu } from 'lucide-react'
import { listUsers, saveUser, saveUserStorage, saveUserTokenLimit } from '../api/client'
import type { AppRole, AppUser, AppUserInput } from '../api/types'
import { useAppUser } from '../components/AppUserContext'
import { Empty, ErrorBanner, Field, LoadingBar, Modal, Pagination } from '../components/ui'
import { useAsync, useDebounced } from '../lib/useAsync'
import { formatDateTime } from '../lib/format'
import { AttachmentStorageUsage } from '../components/AttachmentStorageUsage'
import { MonthlyTokenUsage } from '../components/MonthlyTokenUsage'

const roles: AppRole[] = ['Global Admin', 'Global Reader Admin', 'User']
const BYTES_PER_GB = 1024 ** 3
const TOKENS_PER_MILLION = 1_000_000
const blank: AppUserInput = { email: '', displayName: '', roles: ['User'], isActive: true }

export default function UsersPage() {
  const me = useAppUser()
  const canManage = me.roles.includes('Global Admin')
  const [search, setSearch] = useState('')
  const [skip, setSkip] = useState(0)
  const term = useDebounced(search)
  const page = useAsync(signal => listUsers(term, skip, 25, signal), [term, skip])
  const [editing, setEditing] = useState<AppUser | null>(null)
  const [open, setOpen] = useState(false)
  const [form, setForm] = useState<AppUserInput>(blank)
  const [storageLimitGB, setStorageLimitGB] = useState('')
  const [storageUser, setStorageUser] = useState<AppUser | null>(null)
  const [tokenUser, setTokenUser] = useState<AppUser | null>(null)
  const [tokenLimit, setTokenLimit] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const edit = (user: AppUser | null) => {
    setEditing(user)
    setForm(user ? { email: user.email, displayName: user.displayName, roles: [...user.roles], isActive: user.isActive, concurrencyStamp: user.concurrencyStamp } : { ...blank, roles: [...blank.roles] })
    setError(null)
    setOpen(true)
  }
  const save = async () => {
    setBusy(true)
    setError(null)
    try {
      const saved = await saveUser(editing?.id ?? null, form)
      setOpen(false)
      setNotice(`${saved.displayName} ${editing ? 'updated' : 'created'}.`)
      page.reload()
      if (saved.id === me.id) window.dispatchEvent(new Event('app-profile-refresh'))
    } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) }
    finally { setBusy(false) }
  }
  const manageStorage = (user: AppUser) => {
    setStorageUser(user)
    setStorageLimitGB(user.attachmentStorageLimitBytes == null ? '' : String(user.attachmentStorageLimitBytes / BYTES_PER_GB))
    setError(null)
  }
  const saveStorage = async () => {
    if (!storageUser) return
    setBusy(true)
    setError(null)
    try {
      const limit = storageLimitGB.trim() === '' ? null : Math.round(Number(storageLimitGB) * BYTES_PER_GB)
      if (limit !== null && (!Number.isSafeInteger(limit) || limit < 0)) throw new Error('Enter a valid non-negative storage limit.')
      const saved = await saveUserStorage(storageUser.id, {
        concurrencyStamp: storageUser.concurrencyStamp,
        attachmentStorageLimitBytes: limit,
      })
      setStorageUser(null)
      setNotice(`Storage limit updated for ${saved.displayName}.`)
      page.reload()
      if (saved.id === me.id) window.dispatchEvent(new Event('app-profile-refresh'))
    } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) }
    finally { setBusy(false) }
  }
  const saveTokens = async () => {
    if (!tokenUser) return
    setBusy(true)
    setError(null)
    try {
      const millions = tokenLimit.trim() === '' ? null : Number(tokenLimit)
      const limit = millions === null ? null : millions * TOKENS_PER_MILLION
      if (millions !== null && (!Number.isSafeInteger(millions) || millions < 0 || !Number.isSafeInteger(limit))) throw new Error('Enter a non-negative whole number of millions of tokens.')
      const saved = await saveUserTokenLimit(tokenUser.id, { monthlyTokenLimit: limit, concurrencyStamp: tokenUser.concurrencyStamp })
      setTokenUser(null)
      setNotice(`Monthly token limit updated for ${saved.displayName}.`)
      page.reload()
      if (saved.id === me.id) window.dispatchEvent(new Event('app-profile-refresh'))
    } catch (cause) { setError(cause instanceof Error ? cause.message : String(cause)) }
    finally { setBusy(false) }
  }
  return <div className="stack">
    <div className="page-head">
      <div><h1><Users size={20} />Users</h1><p>Manage app access, roles, and usage limits. Roles are separate from Entra ID.</p></div>
      <div className="row"><button onClick={page.reload}><RefreshCw size={14} />Refresh</button>
        {canManage && <button onClick={() => edit(null)}><UserPlus size={14} />Create user</button>}</div>
    </div>
    {!canManage && <p className="banner">You have read-only access to user management.</p>}
    {notice && <p role="status">{notice}</p>}
    <div className="card card-body"><Field label="Search users"><input type="search" value={search} placeholder="Name or email" onChange={e => { setSearch(e.target.value); setSkip(0) }} /></Field></div>
    <LoadingBar active={page.loading} />
    {page.error && <ErrorBanner message={page.error} onRetry={page.reload} />}
    <div className="card">
      <div className="table-scroll"><table><thead><tr><th>User</th><th>Application roles</th><th>Attachment storage</th><th>Monthly chat tokens</th><th>Status</th><th>Entra sign-in</th><th>Last sign-in</th>{canManage && <th>Actions</th>}</tr></thead>
        <tbody>{page.data?.items.map(user => <tr key={user.id}>
          <td><strong>{user.displayName}</strong><div>{user.email}</div></td>
          <td><div className="row">{user.roles.map(role => <span key={role} className="badge"><ShieldCheck size={12} />{role}</span>)}</div></td>
          <td><AttachmentStorageUsage used={user.attachmentStorageUsedBytes} limit={user.attachmentStorageLimitBytes} /></td>
          <td><MonthlyTokenUsage user={user} /></td>
          <td><span className={`badge ${user.isActive ? 'good' : 'warning'}`}>{user.isActive ? 'Active' : 'Disabled'}</span></td>
          <td>{user.hasSignedIn ? 'Linked' : 'Awaiting first sign-in'}</td>
          <td>{formatDateTime(user.lastLoginAtUtc)}</td>
          {canManage && <td><div className="row">
            <button onClick={() => edit(user)} aria-label={`Edit ${user.email}`}><Pencil size={13} />Edit</button>
            <button onClick={() => manageStorage(user)} aria-label={`Manage storage for ${user.email}`}><HardDrive size={13} />Manage storage</button>
            <button onClick={() => { setTokenUser(user); setTokenLimit(user.monthlyTokenLimit == null ? '' : String(user.monthlyTokenLimit / TOKENS_PER_MILLION)); setError(null) }} aria-label={`Manage tokens for ${user.email}`}><Cpu size={13} />Manage tokens</button>
          </div></td>}
        </tr>)}</tbody></table></div>
      {page.data?.items.length === 0 && <Empty title="No users found" />}
      <Pagination skip={skip} top={25} total={page.data?.totalCount ?? 0} onSkip={setSkip} />
    </div>
    <Modal open={open} title={editing ? 'Edit user' : 'Create user'} icon={<Users size={18} />} onClose={() => { if (!busy) setOpen(false) }} footer={<>
      <button disabled={busy} onClick={() => setOpen(false)}><X size={14} aria-hidden="true" />Cancel</button>
      <button disabled={busy || form.roles.length === 0} type="submit" form="user-editor">
        {busy ? <LoaderCircle size={14} className="auth-spinner" aria-hidden="true" /> : editing ? <Save size={14} aria-hidden="true" /> : <UserPlus size={14} aria-hidden="true" />}
        {busy ? 'Saving…' : editing ? 'Save changes' : 'Create user'}
      </button>
    </>}>
      <form id="user-editor" className="stack" onSubmit={event => { event.preventDefault(); void save() }}>
        <p>Pre-create an account using the person's primary directory email. Their assigned roles will apply on first Entra sign-in. No password or invitation is sent.</p>
        <Field label="Display name"><input type="text" required maxLength={200} value={form.displayName} onChange={e => setForm({ ...form, displayName: e.target.value })} /></Field>
        <Field label="Email" help={editing?.hasSignedIn ? 'Linked to an Entra account; email cannot be changed here.' : undefined}><input type="email" required maxLength={254} disabled={editing?.hasSignedIn} value={form.email} onChange={e => setForm({ ...form, email: e.target.value })} /></Field>
        <fieldset className="user-roles" disabled={busy}>
          <legend>Application roles</legend>
          {roles.map(role => <label className="user-role-option" key={role}>
            <input type="checkbox" checked={form.roles.includes(role)} onChange={e => setForm(current => ({ ...current, roles: e.target.checked ? [...current.roles, role] : current.roles.filter(value => value !== role) }))} /> {role}
          </label>)}
          <p className="hint">Select at least one role. Permissions from selected roles are combined.</p>
        </fieldset>
        <label><input type="checkbox" checked={form.isActive} onChange={e => setForm({ ...form, isActive: e.target.checked })} /> Account active</label>
        <p>Global Admin manages the application. Global Reader Admin can view administration pages. User can search, chat, and manage their own uploads.</p>
        {error && <ErrorBanner message={error} />}
      </form>
    </Modal>
    <Modal open={storageUser !== null} title="Manage attachment storage" icon={<HardDrive size={18} />} onClose={() => { if (!busy) setStorageUser(null) }} footer={<>
      <button disabled={busy} onClick={() => setStorageUser(null)}><X size={14} aria-hidden="true" />Cancel</button>
      <button disabled={busy} type="submit" form="storage-editor">
        {busy ? <LoaderCircle size={14} className="auth-spinner" aria-hidden="true" /> : <Save size={14} aria-hidden="true" />}
        {busy ? 'Saving…' : 'Save storage limit'}
      </button>
    </>}>
      {storageUser && <form id="storage-editor" className="stack" onSubmit={event => { event.preventDefault(); void saveStorage() }}>
        <div><strong>{storageUser.displayName}</strong><div>{storageUser.email}</div></div>
        <AttachmentStorageUsage used={storageUser.attachmentStorageUsedBytes} limit={storageUser.attachmentStorageLimitBytes} />
        <Field label="Attachment storage limit (GB)" help="Leave blank for unlimited. Set 0 to block new uploads. Lowering the limit does not delete existing files.">
          <input type="number" min={0} step={1} placeholder="Unlimited" disabled={busy} value={storageLimitGB} onChange={e => setStorageLimitGB(e.target.value)} />
        </Field>
        {error && <ErrorBanner message={error} />}
      </form>}
    </Modal>
    <Modal open={tokenUser !== null} title="Manage monthly tokens" icon={<Cpu size={18} />} onClose={() => { if (!busy) setTokenUser(null) }} footer={<>
      <button disabled={busy} onClick={() => setTokenUser(null)}><X size={14} />Cancel</button>
      <button disabled={busy} type="submit" form="token-editor"><Save size={14} />{busy ? 'Saving…' : 'Save token limit'}</button>
    </>}>
      {tokenUser && <form id="token-editor" className="stack" onSubmit={event => { event.preventDefault(); void saveTokens() }}>
        <div><strong>{tokenUser.displayName}</strong><div>{tokenUser.email}</div></div>
        <MonthlyTokenUsage user={tokenUser} showDetails />
        <Field label="Monthly chat token limit (millions)" help="Enter whole millions: 1 = 1,000,000 tokens. Includes input and output tokens across tool-call rounds. Blank means unlimited; 0 blocks new chat turns. Resets on the first day of each month at 00:00 UTC.">
          <input type="number" min={0} step={1} placeholder="Unlimited" disabled={busy} value={tokenLimit} onChange={e => setTokenLimit(e.target.value)} />
        </Field>
        <p className="hint">An in-progress response can exceed the remaining allowance. Changes apply to subsequent turns and do not reset usage.</p>
        {error && <ErrorBanner message={error} />}
      </form>}
    </Modal>
  </div>
}
