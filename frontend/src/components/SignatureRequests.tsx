import { useRef, useState } from 'react'
import { Download, ExternalLink, Eye, FilePenLine, FilePlus2, Plus, RefreshCw, Signature, Trash2 } from 'lucide-react'
import { createSignatureRequest, downloadSignatureDocument, getSigningProviders, listSignatureRequests, prepareSignatureRequest, refreshSignatureRequest } from '../api/client'
import { useAsync } from '../lib/useAsync'
import { ErrorBanner, LoadingBar, Modal } from './ui'
import { PdfPreview } from './PdfPreview'

function SigningProvider({ provider }: { provider: string }) {
  const adobe = provider === 'AdobeSign'
  const Icon = adobe ? FilePenLine : Signature

  return <span className="signature-provider-label">
    <Icon size={18} aria-hidden="true" className={adobe ? 'signature-provider-icon adobe-sign' : 'signature-provider-icon docusign'} />
    {adobe ? 'Adobe Acrobat Sign' : provider}
  </span>
}

export function SignatureRequests({ id, name, readOnly, onClose }: {
  id: string; name: string; readOnly: boolean; onClose: () => void
}) {
  const providers = useAsync(getSigningProviders, [])
  const [revision, setRevision] = useState(0)
  const requests = useAsync(signal => listSignatureRequests(id, signal), [id, revision])
  const [provider, setProvider] = useState('')
  const [subject, setSubject] = useState(name.slice(0, 100))
  const [message, setMessage] = useState('')
  const [recipients, setRecipients] = useState([{ name: '', email: '' }])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [preparationUrl, setPreparationUrl] = useState<string | null>(null)
  const [createdDraft, setCreatedDraft] = useState(false)
  const [preview, setPreview] = useState<{ requestId: string; audit: boolean } | null>(null)
  const clientRequestId = useRef(crypto.randomUUID())
  const pending = useRef(false)
  const selectedProvider = provider || providers.data?.[0] || ''

  const run = async (action: () => Promise<void>) => {
    if (pending.current) {
      return
    }
    pending.current = true
    setBusy(true)
    setError(null)
    setPreparationUrl(null)
    try {
      await action()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      pending.current = false
      setBusy(false)
      setRevision(value => value + 1)
    }
  }

  const prepare = async (requestId: string) => {
    const result = await prepareSignatureRequest(id, requestId)
    setPreparationUrl(result.url)
  }

  const download = async (requestId: string, audit: boolean) => {
    const blob = await downloadSignatureDocument(id, requestId, audit)
    const url = URL.createObjectURL(blob)
    const link = document.createElement('a')
    link.href = url
    link.download = `${audit ? 'audit' : 'signed'}-${name}`
    document.body.appendChild(link)
    link.click()
    link.remove()
    window.setTimeout(() => URL.revokeObjectURL(url), 1000)
  }

  return <><Modal open title={`Signatures — ${name}`} className="signature-requests-modal" onClose={onClose}>
    <LoadingBar active={busy || requests.loading || providers.loading} />
    {(error || requests.error || providers.error) && <ErrorBanner message={error || requests.error || providers.error || ''} />}
    {preparationUrl && <p><a className="button-link" href={preparationUrl} target="_blank" rel="noopener noreferrer">
      <ExternalLink size={14} />Open preparation screen
    </a> Place fields and send there, then return here and refresh the request status.</p>}
    {!readOnly && <form onSubmit={event => {
      event.preventDefault()
      void run(async () => {
        const row = await createSignatureRequest(id, { provider: selectedProvider, subject, message, recipients, clientRequestId: clientRequestId.current })
        setCreatedDraft(true)
        if (!row.externalId) {
          throw new Error('This request needs administrator review in the provider account. Do not create a replacement until it has been checked.')
        }
        await prepare(row.id)
      })
    }}>
      <fieldset disabled={busy || createdDraft || !providers.data?.length} className="signature-form">
        <legend>New signing request</legend>
        <p>Send through the shared company account. Recipients sign in the order listed. Creating a draft uploads this PDF to the selected provider; you review and send it in their preparation screen.</p>
        <div className="signature-provider-field">
          <span>Provider</span>
          <div className="signature-provider-options" role="group" aria-label="Signing provider">
            {providers.data?.map(value => <button key={value} type="button"
              aria-pressed={selectedProvider === value} onClick={() => setProvider(value)}>
              <SigningProvider provider={value} />
            </button>)}
          </div>
        </div>
        <label>Subject<input type="text" value={subject} maxLength={100} required onChange={event => setSubject(event.target.value)} /></label>
        <label>Message<textarea value={message} maxLength={2000} onChange={event => setMessage(event.target.value)} /></label>
        {recipients.map((recipient, index) => <div className="signature-recipient" key={index}>
          <label>Signer {index + 1} name<input type="text" required maxLength={100} value={recipient.name}
            onChange={event => setRecipients(values => values.map((value, i) => i === index ? { ...value, name: event.target.value } : value))} /></label>
          <label>Email<input type="email" required maxLength={254} value={recipient.email}
            onChange={event => setRecipients(values => values.map((value, i) => i === index ? { ...value, email: event.target.value } : value))} /></label>
          <button type="button" disabled={recipients.length === 1} aria-label={`Remove signer ${index + 1}`}
            onClick={() => setRecipients(values => values.filter((_, i) => i !== index))}><Trash2 size={14} /></button>
        </div>)}
        <div className="row">
          <button type="button" disabled={recipients.length >= 20} onClick={() => setRecipients(values => [...values, { name: '', email: '' }])}><Plus size={14} />Add signer</button>
          <button type="submit"><FilePlus2 size={14} aria-hidden="true" />Create draft</button>
        </div>
      </fieldset>
      {createdDraft && <button type="button" disabled={busy} onClick={() => {
        clientRequestId.current = crypto.randomUUID()
        setCreatedDraft(false)
        setPreparationUrl(null)
      }}><Plus size={14} />Start another signing request</button>}
      {providers.data?.length === 0 && <p>No signing providers are enabled. Ask an administrator to configure the shared organization connection.</p>}
    </form>}
    <div className="row"><h3>Signing requests</h3><button disabled={busy} onClick={() => setRevision(value => value + 1)}><RefreshCw size={14} />Reload list</button></div>
    {requests.data?.length === 0 && <p>No signing requests yet.</p>}
    {requests.data?.map(row => <section className="signature-request" key={row.id}>
      <strong>{row.subject}</strong><p><SigningProvider provider={row.provider} /> · {row.status} · {new Date(row.createdAtUtc).toLocaleString()}</p>
      {row.status === 'NeedsReview' || row.status === 'Creating' ? <p>Ask an administrator to check the provider account using reference {row.id}. The result of draft creation has not been confirmed.</p> : null}
      <div className="row">
        {!readOnly && row.externalId && <button disabled={busy} onClick={() => void run(async () => { await refreshSignatureRequest(id, row.id) })}><RefreshCw size={14} />Refresh status</button>}
        {!readOnly && row.externalId && ['Draft', 'created', 'DRAFT', 'AUTHORING'].includes(row.status) && <button disabled={busy} onClick={() => void run(() => prepare(row.id))}><ExternalLink size={14} />Prepare and send</button>}
        {['completed', 'SIGNED'].includes(row.status) && <>
          <button disabled={busy} onClick={() => setPreview({ requestId: row.id, audit: false })}><Eye size={14} />Preview signed PDF</button>
          <button disabled={busy} onClick={() => setPreview({ requestId: row.id, audit: true })}><Eye size={14} />Preview audit record</button>
          <button disabled={busy} onClick={() => void run(() => download(row.id, false))}><Download size={14} />Signed PDF</button>
          <button disabled={busy} onClick={() => void run(() => download(row.id, true))}><Download size={14} />Audit record</button>
        </>}
      </div>
    </section>)}
  </Modal>
    {preview && <PdfPreview
      key={`${preview.requestId}:${preview.audit}`}
      name={`${preview.audit ? 'audit' : 'signed'}-${name}`}
      sourceKey={`${id}:${preview.requestId}:${preview.audit}`}
      load={signal => downloadSignatureDocument(id, preview.requestId, preview.audit, signal)}
      onClose={() => setPreview(null)}
    />}
  </>
}
