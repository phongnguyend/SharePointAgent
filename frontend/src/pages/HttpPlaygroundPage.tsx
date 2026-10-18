import { usePlaygroundMaximized } from '../components/usePlaygroundMaximized'
import '../components/PlaygroundRequestTabs.css'
import { useCallback, useEffect, useId, useRef, useState, type Dispatch, type ReactNode, type SetStateAction } from 'react'
import { Braces, Cookie, Copy, Download, FileText, Globe, GripVertical, KeyRound, List, ListFilter, LoaderCircle, LockKeyhole, Maximize2, Minimize2, Plus, Save, Send, Settings, ShieldOff, Square, Trash2, Upload, UserRound, X } from 'lucide-react'
import { useCopy } from '../lib/useCopy'
import { request } from '../api/http'
import { CopyButton, ErrorBanner } from '../components/ui'
import { JsonCodeBlock, JsonEditor } from '../components/JsonCode'
import { useToast } from '../components/Toast'
import './HttpPlaygroundPage.css'

type Pair = { name: string; value: string; enabled: boolean }
type Draft = { name: string; method: string; url: string; params: Pair[]; headers: Pair[]; variables: Pair[];
  auth: string; token: string; username: string; password: string; bodyType: string; body: string; form: Pair[]; timeout: number }
type ResponseData = { statusCode: number; reason: string; version: string; durationMs: number; sizeBytes: number;
  headers: { name: string; value: string }[]; body: string; base64: string; truncated: boolean }
const methods = ['GET', 'POST', 'PUT', 'PATCH', 'DELETE', 'HEAD', 'OPTIONS']
const pair = (): Pair => ({ name: '', value: '', enabled: true })
const blank = (): Draft => ({ name: 'Untitled request', method: 'GET', url: '',
  params: Array.from({ length: 3 }, pair), headers: Array.from({ length: 3 }, pair), variables: Array.from({ length: 3 }, pair),
  auth: 'none', token: '', username: '', password: '', bodyType: 'none', body: '', form: Array.from({ length: 3 }, pair), timeout: 30 })

function Pairs({ title, rows, change }: { title: string; rows: Pair[]; change: (rows: Pair[]) => void }) {
  const source = useRef<number | null>(null)
  const [target, setTarget] = useState<number | null>(null)
  const [announcement, setAnnouncement] = useState('')
  const list = useRef<HTMLDivElement>(null)
  const reset = () => {
    source.current = null
    setTarget(null)
  }
  const move = (from: number, to: number) => {
    if (from === to || to < 0 || to >= rows.length || from < 0 || from >= rows.length) {
      return
    }
    const next = [...rows]
    const [row] = next.splice(from, 1)
    next.splice(to, 0, row)
    change(next)
    setAnnouncement(`${title} moved from position ${from + 1} to ${to + 1}.`)
    requestAnimationFrame(() => list.current?.querySelectorAll<HTMLButtonElement>('.http-row-drag')[to]?.focus())
  }
  return <div ref={list} className="stack http-pairs" onDragLeave={event => {
    if (!(event.relatedTarget instanceof Node) || !event.currentTarget.contains(event.relatedTarget)) {
      setTarget(null)
    }
  }}>
    <span className="http-reorder-status" role="status">{announcement}</span>
    {rows.map((row, index) => <div className="http-pair" key={index} data-drop-target={target === index}
      onDragOver={event => {
        if (source.current !== null && !event.currentTarget.closest('fieldset[disabled]')) {
          event.preventDefault()
          event.dataTransfer.dropEffect = 'move'
          setTarget(index)
        }
      }} onDrop={event => {
        if (source.current !== null) {
          event.preventDefault()
          if (!event.currentTarget.closest('fieldset[disabled]')) {
            move(source.current, index)
          }
          reset()
        }
      }}>
      <button type="button" draggable className="ghost icon-only http-row-drag"
        aria-label={`Reorder ${title} ${index + 1}`} title="Drag to reorder, or use Alt + Arrow Up/Down"
        onDragStart={event => {
          if (event.currentTarget.matches(':disabled')) {
            event.preventDefault()
            return
          }
          source.current = index
          event.dataTransfer.effectAllowed = 'move'
          event.dataTransfer.setData('text/plain', String(index))
        }} onDragEnd={reset} onKeyDown={event => {
          if (event.altKey && (event.key === 'ArrowUp' || event.key === 'ArrowDown')) {
            event.preventDefault()
            move(index, index + (event.key === 'ArrowUp' ? -1 : 1))
          }
          if (event.key === 'Escape') {
            reset()
          }
        }}><GripVertical size={15} aria-hidden="true" /></button>
      <input type="checkbox" aria-label={`Enable ${title} ${index + 1}`} checked={row.enabled} onChange={event => change(rows.map((r, i) => i === index ? { ...r, enabled: event.target.checked } : r))} />
      <input aria-label={`${title} name ${index + 1}`} placeholder="Name" value={row.name} onChange={event => change(rows.map((r, i) => i === index ? { ...r, name: event.target.value } : r))} />
      <input aria-label={`${title} value ${index + 1}`} placeholder="Value" value={row.value} onChange={event => change(rows.map((r, i) => i === index ? { ...r, value: event.target.value } : r))} />
      <button type="button" className="ghost icon-only" aria-label={`Remove ${title} ${index + 1}`} onClick={() => change(rows.filter((_, i) => i !== index))}><Trash2 size={15} /></button>
    </div>)}
    <button type="button" className="http-add" onClick={() => change([...rows, pair()])}><Plus size={14} />Add {title}</button>
  </div>
}

function build(draft: Draft) {
  const variables = new Map(draft.variables.filter(r => r.enabled && r.name).map(r => [r.name, r.value]))
  const expand = (text: string) => text.replace(/\{\{([^{}]+)\}\}/g, (_, key: string) => {
    const value = variables.get(key.trim())
    if (value === undefined) {
      throw new Error(`Missing variable: ${key}`)
    }
    return value
  })
  const url = new URL(expand(draft.url))
  for (const row of draft.params.filter(r => r.enabled && r.name)) {
    url.searchParams.append(expand(row.name), expand(row.value))
  }
  const headers = draft.headers.filter(r => r.enabled && r.name).map(r => ({ name: expand(r.name), value: expand(r.value) }))
  if (draft.auth !== 'none') {
    if (headers.some(h => h.name.toLowerCase() === 'authorization')) {
      throw new Error('Use either the Authentication editor or an Authorization header, not both.')
    }
    const basic = () => btoa(Array.from(new TextEncoder().encode(`${expand(draft.username)}:${expand(draft.password)}`), b => String.fromCharCode(b)).join(''))
    headers.push({ name: 'Authorization', value: draft.auth === 'bearer' ? `Bearer ${expand(draft.token)}` : `Basic ${basic()}` })
  }
  let body: string | null = null
  if (!['GET', 'HEAD'].includes(draft.method) && draft.bodyType !== 'none') {
    body = draft.bodyType === 'form' ? new URLSearchParams(draft.form.filter(r => r.enabled && r.name).map(r => [expand(r.name), expand(r.value)])).toString() : expand(draft.body)
    if (draft.bodyType === 'json') {
      JSON.parse(body)
    }
    if (!headers.some(h => h.name.toLowerCase() === 'content-type')) {
      headers.push({ name: 'Content-Type', value: draft.bodyType === 'json' ? 'application/json' : draft.bodyType === 'form' ? 'application/x-www-form-urlencoded' : 'text/plain; charset=utf-8' })
    }
  }
  return { method: draft.method, url: url.toString(), headers, body, timeoutSeconds: draft.timeout }
}

function download(value: Blob, name: string) {
  const url = URL.createObjectURL(value)
  const link = document.createElement('a')
  link.href = url
  link.download = name
  link.click()
  window.setTimeout(() => URL.revokeObjectURL(url), 1000)
}

type RequestTab = { id: string; method: string; title: string; busy: boolean; initial?: Draft }
const newTab = (initial?: Draft): RequestTab => ({ id: crypto.randomUUID(), method: initial?.method ?? 'GET',
  title: initial?.url || initial?.name || 'Untitled request', busy: false, initial })

export default function HttpPlaygroundPage() {
  const { fullscreen, toggleFullscreen } = usePlaygroundMaximized()
  const [tabs, setTabs] = useState<RequestTab[]>(() => [newTab()])
  const [active, setActive] = useState<string | null>(null)
  const [saved, setSaved] = useState<Draft[]>([])
  const [history, setHistory] = useState<Draft[]>([])
  const tablist = useRef<HTMLDivElement>(null)
  const uid = useId()
  const selected = active ?? tabs[0].id
  const summary = useCallback((id: string, method: string, title: string, busy: boolean) => {
    setTabs(current => current.map(tab => tab.id === id ? { ...tab, method, title, busy } : tab))
  }, [])
  const open = (draft?: Draft) => {
    const tab = newTab(draft ? structuredClone(draft) : undefined)
    setTabs(current => [...current, tab])
    setActive(tab.id)
  }
  const close = (id: string) => {
    const index = tabs.findIndex(tab => tab.id === id)
    const remaining = tabs.filter(tab => tab.id !== id)
    if (!remaining.length) {
      remaining.push(newTab())
    }
    setTabs(remaining)
    if (selected === id) {
      setActive(remaining[Math.min(index, remaining.length - 1)].id)
    }
  }
  useEffect(() => {
    tablist.current?.querySelector('[aria-selected="true"]')?.scrollIntoView({ block: 'nearest', inline: 'nearest' })
  }, [selected])

  const requestTabs = <div className="http-request-tabbar">
      <div ref={tablist} className="http-request-tabs" role="tablist" aria-label="HTTP requests" onKeyDown={event => {
        if (!(event.target instanceof HTMLElement) || event.target.getAttribute('role') !== 'tab') {
          return
        }
        const index = tabs.findIndex(tab => tab.id === selected)
        if (event.key === 'Delete') {
          event.preventDefault()
          close(selected)
          requestAnimationFrame(() => tablist.current?.querySelector<HTMLButtonElement>('[aria-selected="true"]')?.focus())
          return
        }
        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
          return
        }
        event.preventDefault()
        const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1
          : (index + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length
        setActive(tabs[next].id)
        requestAnimationFrame(() => tablist.current?.querySelector<HTMLButtonElement>(`[data-request-id="${tabs[next].id}"]`)?.focus())
      }}>
        {tabs.map(tab => <div key={tab.id} className="http-request-tab" role="presentation" data-selected={selected === tab.id}>
          <button type="button" role="tab" id={`${uid}-${tab.id}`} data-request-id={tab.id}
            aria-controls={`${uid}-${tab.id}-panel`} aria-selected={selected === tab.id} tabIndex={selected === tab.id ? 0 : -1}
            title={`${tab.method} ${tab.title}`} onClick={() => setActive(tab.id)}>
            <span className="http-method" data-method={tab.method}>{tab.method}</span><span className="http-tab-title">{tab.title}</span>
            {tab.busy && <LoaderCircle size={13} className="http-tab-loading" aria-label="Request running" />}
          </button>
          <button type="button" className="ghost icon-only http-close-tab" aria-label={`Close ${tab.method} ${tab.title}`}
            title={tab.busy ? 'Close tab and cancel request' : 'Close request tab'} onClick={() => close(tab.id)}><X size={13} /></button>
        </div>)}
      </div>
      <button type="button" className="ghost icon-only" aria-label="New request tab" title="New request tab" onClick={() => open()}><Plus size={17} /></button>
    </div>

  return <div className={`stack http-request-workspace playground-request-container${fullscreen ? ' is-maximized' : ''}`}>
    {tabs.map(tab => <div key={tab.id} hidden={selected !== tab.id}>
      <HttpRequestEditor id={tab.id} initial={tab.initial} onSummary={summary} onNew={open}
        fullscreen={fullscreen} onToggleFullscreen={toggleFullscreen} requestTabs={selected === tab.id ? requestTabs : null} panelId={`${uid}-${tab.id}-panel`} tabId={`${uid}-${tab.id}`}
        saved={saved} setSaved={setSaved} history={history} setHistory={setHistory} />
    </div>)}
  </div>
}

function HttpRequestEditor({ id, initial, onSummary, onNew, saved, setSaved, history, setHistory, requestTabs, panelId, tabId, fullscreen, onToggleFullscreen }: {
  id: string; initial?: Draft; onSummary: (id: string, method: string, title: string, busy: boolean) => void
  onNew: (draft?: Draft) => void; saved: Draft[]; setSaved: Dispatch<SetStateAction<Draft[]>>
  history: Draft[]; setHistory: Dispatch<SetStateAction<Draft[]>>
  fullscreen: boolean; onToggleFullscreen: () => void
  requestTabs: ReactNode; panelId: string; tabId: string
}) {
  const [draft, setDraft] = useState<Draft>(() => initial ?? blank())
  const [tab, setTab] = useState('Parameters')
  const [responseTab, setResponseTab] = useState('Body')
  const [bodyView, setBodyView] = useState('Pretty')
  const [result, setResult] = useState<ResponseData | null>(null)
  const [sent, setSent] = useState('')
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const pending = useRef<AbortController | null>(null)
  const upload = useRef<HTMLInputElement>(null)
  const toast = useToast()
  const copy = useCopy()
  const update = <K extends keyof Draft,>(key: K, value: Draft[K]) => setDraft(current => ({ ...current, [key]: value }))
  useEffect(() => () => pending.current?.abort(), [])
  useEffect(() => {
    onSummary(id, draft.method, draft.name && draft.name !== 'Untitled request' ? draft.name : draft.url || 'Untitled request', busy)
  }, [id, draft.method, draft.name, draft.url, busy, onSummary])

  async function send() {
    if (pending.current) {
      return
    }
    setError('')
    setResult(null)
    const controller = new AbortController()
    pending.current = controller
    setBusy(true)
    try {
      const input = build(draft)
      setSent(`${input.method} ${input.url}`)
      setHistory(current => [structuredClone(draft), ...current].slice(0, 20))
      setResult(await request<ResponseData>('/api/admin/http-playground', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(input), signal: controller.signal }))
    } catch (cause) {
      setError(controller.signal.aborted ? 'Request cancelled. The remote server may still complete the operation.' : cause instanceof Error ? cause.message : 'Request failed.')
    } finally {
      pending.current = null
      setBusy(false)
    }
  }

  async function importFile(file?: File) {
    if (!file) {
      return
    }
    try {
      if (file.size > 3 * 1024 * 1024) {
        throw new Error('Request files must be at most 3 MB.')
      }
      const data = JSON.parse(await file.text()) as Draft
      const template = blank()
      for (const key of Object.keys(template) as (keyof Draft)[]) {
        if (Array.isArray(template[key])) {
          if (!Array.isArray(data[key]) || (data[key] as Pair[]).some(r => !r || typeof r.name !== 'string' || typeof r.value !== 'string' || typeof r.enabled !== 'boolean')) {
            throw new Error('Invalid request rows.')
          }
        } else if (typeof data[key] !== typeof template[key]) {
          throw new Error(`Invalid request field: ${key}`)
        }
      }
      if (!methods.includes(data.method) || !['none', 'basic', 'bearer'].includes(data.auth) || !['none', 'json', 'text', 'form'].includes(data.bodyType) || data.timeout < 1 || data.timeout > 120) {
        throw new Error('Unsupported request settings.')
      }
      setDraft(data)
      setError('')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not import request.')
    }
  }

  let pretty = result?.body ?? ''
  try {
    pretty = JSON.stringify(JSON.parse(pretty), null, 2)
  } catch {
    // Plain text, XML, and HTML are shown as text, never executed.
  }

  return <section className="card http-playground">
    <div className="card-head http-toolbar">
      <h2><Globe size={18} />HTTP playground</h2>
      <div className="row http-header-actions">
      <button disabled={busy} onClick={() => upload.current?.click()}><Upload size={15} />Import</button>
      <button onClick={() => download(new Blob([JSON.stringify(draft, null, 2)], { type: 'application/json' }), 'http-request.json')}><Download size={15} />Export</button>
      <button onClick={() => {
        try {
          const input = build(draft)
          const quote = (value: string) => "'" + value.replace(/'/g, "'\\''") + "'"
          const args = ['curl', '--request', input.method, '--url', quote(input.url), '--max-time', String(input.timeoutSeconds)]
          for (const header of input.headers) {
            args.push('--header', quote(`${header.name}: ${header.value}`))
          }
          if (input.body !== null) {
            args.push('--data-raw', quote(input.body))
          }
          void copy(args.join(' '), 'cURL command')
        } catch (cause) {
          setError(cause instanceof Error ? cause.message : 'Cannot generate cURL command.')
        }
      }}><Copy size={15} />Copy cURL (Bash)</button>
      <input ref={upload} type="file" accept=".json" hidden onChange={event => { void importFile(event.target.files?.[0]); event.target.value = '' }} />
      <button type="button" className="ghost icon-only" style={{ marginLeft: 'auto' }}
        aria-label={fullscreen ? 'Restore playground' : 'Maximize playground'} title={fullscreen ? 'Restore playground (Esc)' : 'Maximize playground'}
        aria-pressed={fullscreen} onClick={onToggleFullscreen}>
        {fullscreen ? <Minimize2 size={17} /> : <Maximize2 size={17} />}
      </button>
    </div>
    </div>
    <div className="card-body stack">
    <p className="decision-description">Public HTTP(S) endpoints only, standard ports. Redirects are not followed. Requests and responses are limited to 2 MB. Saved requests and history last until reload; exported files include entered credentials.</p>
    <div className="http-layout">
      <aside className="card stack http-library">
        {([['Saved requests', saved, setSaved], ['History', history, setHistory]] as const).map(([label, items, change]) => <div className="stack" key={label}>
          <div className="row http-library-heading"><strong>{label}</strong><button className="ghost" disabled={!items.length} onClick={() => change([])}><Trash2 size={14} aria-hidden="true" />Clear</button></div>
          {!items.length && <span className="hint">No requests yet.</span>}
          {items.map((item, index) => <button key={index} className="http-history" onClick={() => onNew(item)}><strong><span className="http-history-method" data-method={item.method}>{item.method}</span> · {item.name}</strong><small>{item.url || 'No URL'}</small></button>)}
        </div>)}
      </aside>
      <div className="stack http-main">
        <section className="playground-request-boundary http-request-panel">
          {requestTabs}
        <form className="stack http-request" role="tabpanel" id={panelId} aria-labelledby={tabId} onSubmit={event => { event.preventDefault(); void send() }}>
          <div className="http-request-name">
            <label>Request name<input value={draft.name} onChange={event => update('name', event.target.value)} /></label>
            <button type="button" disabled={busy} onClick={() => { setSaved(current => [structuredClone(draft), ...current].slice(0, 30)); toast.success('Request saved for this session.') }}><Save size={15} />Save request</button>
          </div>
          <div className="http-url"><select className="http-method-select" data-method={draft.method} aria-label="HTTP method" value={draft.method} disabled={busy} onChange={event => update('method', event.target.value)}>{methods.map(method => <option key={method} data-method={method}>{method}</option>)}</select>
            <input aria-label="Request URL" placeholder="https://api.example.com/resource" required value={draft.url} disabled={busy} onChange={event => update('url', event.target.value)} />
            {busy ? <button type="button" onClick={() => pending.current?.abort()}><Square size={15} />Cancel</button> : <button className="primary" type="submit"><Send size={15} />Send</button>}
          </div>
          <div className="page-tabs page-tabs-secondary" role="tablist" aria-label="Request sections" onKeyDown={event => {
            if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
              return
            }
            event.preventDefault()
            const sections = ['Parameters', 'Headers', 'Authentication', 'Body', 'Variables', 'Settings']
            const current = sections.indexOf(tab)
            const next = event.key === 'Home' ? 0 : event.key === 'End' ? sections.length - 1
              : (current + (event.key === 'ArrowRight' ? 1 : -1) + sections.length) % sections.length
            setTab(sections[next])
            event.currentTarget.querySelectorAll<HTMLButtonElement>('[role="tab"]')[next]?.focus()
          }}>{([
            ['Parameters', ListFilter], ['Headers', List], ['Authentication', LockKeyhole],
            ['Body', FileText], ['Variables', Braces], ['Settings', Settings],
          ] as const).map(([label, Icon]) => <button key={label} type="button" role="tab"
            id={`${id}-section-${label}`} aria-controls={`${id}-section-panel`} aria-selected={tab === label}
            tabIndex={tab === label ? 0 : -1} onClick={() => setTab(label)}><Icon size={15} aria-hidden="true" />{label}</button>)}</div>
          <div role="tabpanel" id={`${id}-section-panel`} aria-labelledby={`${id}-section-${tab}`}>
          <fieldset disabled={busy} className="http-editor">
            {tab === 'Parameters' && <Pairs title="parameter" rows={draft.params} change={value => update('params', value)} />}
            {tab === 'Headers' && <Pairs title="header" rows={draft.headers} change={value => update('headers', value)} />}
            {tab === 'Variables' && <div className="stack"><p className="hint">Use {'{{name}}'} in the URL, parameters, headers, authentication, or body.</p><Pairs title="variable" rows={draft.variables} change={value => update('variables', value)} /></div>}
            {tab === 'Settings' && <label>Timeout (seconds)<input type="number" min={1} max={120} value={draft.timeout} onChange={event => update('timeout', Number(event.target.value))} /></label>}
            {tab === 'Authentication' && <div className="stack">
              <div className="row http-tabs" role="group" aria-label="Authentication type">
                {([['none', 'No authentication', ShieldOff], ['bearer', 'Bearer token', KeyRound], ['basic', 'Basic authentication', UserRound]] as const).map(([value, label, Icon]) =>
                  <button key={value} type="button" aria-pressed={draft.auth === value} onClick={() => update('auth', value)}><Icon size={15} aria-hidden="true" />{label}</button>)}
              </div>
              {draft.auth === 'bearer' && <label>Token<input type="password" autoComplete="off" value={draft.token} onChange={event => update('token', event.target.value)} /></label>}
              {draft.auth === 'basic' && <><label>Username<input autoComplete="off" value={draft.username} onChange={event => update('username', event.target.value)} /></label><label>Password<input type="password" autoComplete="off" value={draft.password} onChange={event => update('password', event.target.value)} /></label></>}
            </div>}
            {tab === 'Body' && (['GET', 'HEAD'].includes(draft.method) ? <p className="hint">This method sends no request body.</p> : <div className="stack">
              <div className="row http-tabs" role="group" aria-label="Body type">
                {([['none', 'None', X], ['json', 'JSON', Braces], ['text', 'Raw text / XML', FileText], ['form', 'Form URL encoded', List]] as const).map(([value, label, Icon]) =>
                  <button key={value} type="button" aria-pressed={draft.bodyType === value} onClick={() => update('bodyType', value)}><Icon size={15} aria-hidden="true" />{label}</button>)}
              </div>
              {draft.bodyType === 'form' ? <Pairs title="field" rows={draft.form} change={value => update('form', value)} /> : draft.bodyType !== 'none' && <>
                {draft.bodyType === 'json' ? <>
                  <div className="row http-json-heading">
                    <label htmlFor={`${id}-json-body`}>Request body</label>
                    <button type="button" onClick={() => {
                      try {
                        update('body', JSON.stringify(JSON.parse(draft.body), null, 2))
                        setError('')
                      } catch {
                        setError('The body is not valid JSON.')
                      }
                    }}><Braces size={15} aria-hidden="true" />Beautify</button>
                  </div>
                  <JsonEditor id={`${id}-json-body`} value={draft.body} onChange={value => update('body', value)} disabled={busy} maxLength={2 * 1024 * 1024} />
                </> : <textarea aria-label="Request body" className="http-code" spellCheck={false} value={draft.body} onChange={event => update('body', event.target.value)} />}
              </>}
            </div>)}
          </fieldset>
          </div>
        </form>
        {error && <ErrorBanner message={error} />}
        <section className="stack http-response" aria-label="HTTP response" aria-busy={busy}>
          <div className="row"><strong>Response</strong>{result && <><span className="http-response-status" data-status={Math.floor(result.statusCode / 100)}>{result.statusCode} {result.reason}</span><span>{result.durationMs.toLocaleString()} ms</span><span>{result.sizeBytes.toLocaleString()} bytes</span><span>HTTP/{result.version}</span></>}</div>
          {result ? <><small className="http-sent"><span className="http-history-method" data-method={sent.split(' ')[0]}>{sent.split(' ')[0]}</span>{sent.slice(sent.indexOf(' '))}</small>{result.truncated && <p role="status">Response truncated at 2 MB. Download contains only the captured bytes.</p>}
            <div className="page-tabs page-tabs-secondary" role="tablist" aria-label="Response views" onKeyDown={event => {
              if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
                return
              }
              event.preventDefault()
              const views = ['Body', 'Headers', 'Cookies']
              const current = views.indexOf(responseTab)
              const next = event.key === 'Home' ? 0 : event.key === 'End' ? views.length - 1
                : (current + (event.key === 'ArrowRight' ? 1 : -1) + views.length) % views.length
              setResponseTab(views[next])
              event.currentTarget.querySelectorAll<HTMLButtonElement>('[role="tab"]')[next]?.focus()
            }}>
              {([['Body', FileText], ['Headers', List], ['Cookies', Cookie]] as const).map(([label, Icon]) => <button key={label} type="button" role="tab"
                id={`${id}-response-${label}`} aria-controls={`${id}-response-panel`} aria-selected={responseTab === label}
                tabIndex={responseTab === label ? 0 : -1} onClick={() => setResponseTab(label)}><Icon size={15} aria-hidden="true" />{label}</button>)}
            </div>
            <div className="stack" role="tabpanel" id={`${id}-response-panel`} aria-labelledby={`${id}-response-${responseTab}`}>
            {responseTab === 'Body' && <div className="row http-body-toolbar">
              <div className="segmented" role="group" aria-label="Response body format">
                {([['Pretty', Braces], ['Raw', FileText]] as const).map(([label, Icon]) => <button key={label} type="button" aria-pressed={bodyView === label} onClick={() => setBodyView(label)}><Icon size={15} aria-hidden="true" />{label}</button>)}
              </div>
              <div className="row http-body-actions"><CopyButton value={result.body} label="Copy" variant="outlined" /><button onClick={() => download(new Blob([Uint8Array.from(atob(result.base64), c => c.charCodeAt(0))]), 'response.bin')}><Download size={15} />Download</button></div>
            </div>}
            {responseTab === 'Cookies' ? <div className="http-response-content">
              {result.headers.some(h => h.name.toLowerCase() === 'set-cookie') ? <table>
                <thead><tr><th>Name</th><th>Value</th><th>Attributes</th></tr></thead>
                <tbody>{result.headers.filter(h => h.name.toLowerCase() === 'set-cookie').map((header, index) => {
                  const [pair, ...attributes] = header.value.split(';')
                  const separator = pair.indexOf('=')
                  const name = separator < 0 ? pair.trim() : pair.slice(0, separator).trim()
                  const value = separator < 0 ? '' : pair.slice(separator + 1).trim()
                  return <tr key={index}>
                    <td><div className="http-header-cell"><span>{name}</span><button type="button" className="ghost icon-only" title="Copy cookie name" aria-label={`Copy cookie name ${name}`} onClick={() => void copy(name, 'Cookie name')}><Copy size={13} aria-hidden="true" /></button></div></td>
                    <td><div className="http-header-cell"><span>{value}</span><button type="button" className="ghost icon-only" title="Copy cookie value" aria-label={`Copy value of cookie ${name}`} onClick={() => void copy(value, 'Cookie value')}><Copy size={13} aria-hidden="true" /></button></div></td>
                    <td><div className="http-header-cell"><span>{attributes.join(';').trim() || '—'}</span><button type="button" className="ghost icon-only" title="Copy full Set-Cookie header" aria-label={`Copy full Set-Cookie header for ${name}`} onClick={() => void copy(header.value, 'Set-Cookie header')}><Copy size={13} aria-hidden="true" /></button></div></td>
                  </tr>
                })}</tbody>
              </table> : <p className="hint">No cookies were set by this response.</p>}
            </div> : responseTab === 'Headers' ? <div className="http-response-content"><table><thead><tr><th>Name</th><th>Value</th></tr></thead><tbody>{result.headers.map((h, i) => <tr key={i}>
              <td><div className="http-header-cell"><span>{h.name}</span><button type="button" className="ghost icon-only" title="Copy header name" aria-label={`Copy header name ${h.name}`} onClick={() => void copy(h.name, 'Header name')}><Copy size={13} aria-hidden="true" /></button></div></td>
              <td><div className="http-header-cell"><span>{h.value}</span><button type="button" className="ghost icon-only" title="Copy header value" aria-label={`Copy value of ${h.name}`} onClick={() => void copy(h.value, 'Header value')}><Copy size={13} aria-hidden="true" /></button></div></td>
            </tr>)}</tbody></table></div> : <div className="http-response-content">{bodyView === 'Pretty' ? <JsonCodeBlock value={pretty} /> : <pre>{result.body || '(Empty body)'}</pre>}</div>}
            </div>
          </> : <p className="hint">{busy ? 'Waiting for response…' : 'Send a request to inspect its response.'}</p>}
        </section>
        </section>
      </div>
    </div>
    </div>
  </section>
}
