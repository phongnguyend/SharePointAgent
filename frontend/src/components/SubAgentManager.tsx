import { useEffect, useId, useState } from 'react'
import { createPortal } from 'react-dom'
import { Bot, Check, Pencil, Plus, Trash2, Wrench, BookOpen } from 'lucide-react'
import { deleteSubAgent, getAgentCapabilities, listSubAgents, saveSubAgent } from '../api/agentClient'
import type { SubAgentDefinition, SubAgentInput } from '../api/agentClient'
import type { AgentDefinition } from '../api/types'
import { useAsync } from '../lib/useAsync'
import { useToast } from './Toast'
import { Empty, ErrorBanner, Field, LoadingBar, Modal } from './ui'

const editorTabs = ['general', 'tools', 'skills'] as const
type EditorTab = typeof editorTabs[number]

export default function SubAgentManager({ parent, readOnly, active, footerTarget, onBusyChange, onCountChange }: {
  parent: AgentDefinition
  readOnly: boolean
  active: boolean
  footerTarget: HTMLDivElement | null
  onBusyChange: (busy: boolean) => void
  onCountChange: (count: number) => void
}) {
  const items = useAsync(signal => listSubAgents(parent.id, signal), [parent.id])
  const catalog = useAsync(getAgentCapabilities, [])
  const [editing, setEditing] = useState<SubAgentDefinition | 'new' | null>(null)
  const [removing, setRemoving] = useState<SubAgentDefinition | null>(null)
  const [form, setForm] = useState<SubAgentInput>({ name: '', description: '', modelId: parent.modelId, instructions: '', tools: [], skills: [] })
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [tab, setTab] = useState<EditorTab>('general')
  const tabId = useId()
  const toast = useToast()
  useEffect(() => {
    if (items.data) {
      onCountChange(items.data.length)
    }
  }, [items.data, onCountChange])
  useEffect(() => {
    onBusyChange(busy)
    return () => onBusyChange(false)
  }, [busy, onBusyChange])
  const edit = (item: SubAgentDefinition | 'new') => {
    setForm(item === 'new' ? { name: '', description: '', modelId: parent.modelId, instructions: '', tools: [], skills: [] } : { ...item })
    setError(null)
    setTab('general')
    setEditing(item)
  }
  const save = async () => {
    if (!editing) {
      return
    }
    setBusy(true)
    setError(null)
    try {
      await saveSubAgent(parent.id, editing === 'new' ? null : editing.id, form)
      toast.success(editing === 'new' ? 'Sub-agent created.' : 'Sub-agent updated.')
      setEditing(null)
      items.reload()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(false)
    }
  }
  const remove = async () => {
    if (!removing) {
      return
    }
    setBusy(true)
    setError(null)
    try {
      await deleteSubAgent(parent.id, removing.id)
      toast.success('Sub-agent deleted.')
      setRemoving(null)
      items.reload()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(false)
    }
  }
  const toggle = (kind: 'tools' | 'skills', name: string) => {
    setForm(current => ({ ...current, [kind]: current[kind].includes(name)
      ? current[kind].filter(value => value !== name) : [...current[kind], name] }))
  }
  const editorContent = (
<>
          <div className="sub-agent-tabs" role="tablist" aria-label="Sub-agent configuration">
            {editorTabs.map((name, index) => <button key={name} type="button" role="tab"
              id={`${tabId}-${name}`} aria-controls={`${tabId}-${name}-panel`} aria-selected={tab === name}
              tabIndex={tab === name ? 0 : -1} className={tab === name ? 'primary' : 'ghost'}
              onClick={() => setTab(name)} onKeyDown={event => {
                let next: EditorTab
                if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
                  next = editorTabs[(index + (event.key === 'ArrowRight' ? 1 : editorTabs.length - 1)) % editorTabs.length]
                } else if (event.key === 'Home') {
                  next = editorTabs[0]
                } else if (event.key === 'End') {
                  next = editorTabs[editorTabs.length - 1]
                } else {
                  return
                }
                event.preventDefault()
                setTab(next)
                document.getElementById(`${tabId}-${next}`)?.focus()
              }}>
              {name === 'general' ? <Bot size={14} /> : name === 'tools' ? <Wrench size={14} /> : <BookOpen size={14} />}
              {name === 'general' ? 'General' : `${name === 'tools' ? 'Tools' : 'Skills'} (${form[name].length})`}
            </button>)}
          </div>
          <div role="tabpanel" id={`${tabId}-general-panel`} aria-labelledby={`${tabId}-general`} hidden={tab !== 'general'}>
          <fieldset className="sub-agent-form stack" disabled={readOnly || busy} aria-label="General configuration">
          <div className="sub-agent-fields">
            <Field label="Name"><input value={form.name} maxLength={100} onChange={event => setForm({ ...form, name: event.target.value })} placeholder="Contract reviewer" /></Field>
            <Field label="Model ID"><input value={form.modelId} maxLength={200} onChange={event => setForm({ ...form, modelId: event.target.value })} /></Field>
          </div>
          <Field label="When to invoke" help="The parent uses this description to decide when to call this specialist."><input value={form.description} maxLength={1000}
            onChange={event => setForm({ ...form, description: event.target.value })} placeholder="Review contracts for missing clauses and summarize risks." /></Field>
          <Field label="Instructions"><textarea className="agent-instructions-editor" rows={8} value={form.instructions} maxLength={50000}
            onChange={event => setForm({ ...form, instructions: event.target.value })} /></Field>
          </fieldset>
          </div>
            {(['tools', 'skills'] as const).map(kind => <div role="tabpanel" key={kind}
              id={`${tabId}-${kind}-panel`} aria-labelledby={`${tabId}-${kind}`} hidden={tab !== kind}>
              <fieldset className="sub-agent-form stack" disabled={readOnly || busy} aria-label={kind === 'tools' ? 'Tool selection' : 'Skill selection'}>
              <div className="sub-agent-selection-toolbar">
                <span className="hint">{form[kind].length} selected · {catalog.data?.[kind].length ?? 0} available</span>
                {!readOnly ? <div className="row">
                  <button type="button" disabled={catalog.loading || !!catalog.error || !catalog.data?.[kind].length || catalog.data[kind].every(item => form[kind].includes(item.name))}
                    onClick={() => setForm(current => ({ ...current, [kind]: [...new Set([...current[kind], ...(catalog.data?.[kind].map(item => item.name) ?? [])])] }))}>Select all</button>
                  <button type="button" disabled={form[kind].length === 0} onClick={() => setForm(current => ({ ...current, [kind]: [] }))}>Unselect all</button>
                </div> : null}
              </div>
              <p className="hint">{kind === 'tools' ? 'Selected tools require their services and workspace mode to be available.' : 'Selected skills include their deployed scripts and resources.'} No selections means none.</p>
              <div className="sub-agent-options">
                {catalog.data?.[kind].map(item => <label key={item.name} className="sub-agent-option">
                  <input type="checkbox" checked={form[kind].includes(item.name)} onChange={() => toggle(kind, item.name)} />
                  <span><strong>{item.name}</strong><small>{item.description}</small></span>
                </label>)}
                {form[kind].filter(name => !catalog.data?.[kind].some(item => item.name === name)).map(name => <label key={name} className="sub-agent-option">
                  <input type="checkbox" checked onChange={() => toggle(kind, name)} /><span>{name}<small>Not available in this deployment. Deselect to remove.</small></span>
                </label>)}
                {catalog.data?.[kind].length === 0 ? <p className="hint">No {kind} deployed.</p> : null}
              </div>
              </fieldset>
            </div>)}
        </>
  )
  return <>
    {active && footerTarget && !readOnly ? createPortal(
      <button className="primary" onClick={() => edit('new')}><Plus size={14} />New sub-agent</button>, footerTarget) : null}
      <div className="stack">
        <p className="hint">Only {parent.name} can invoke these specialists as tools. They share the conversation workspace and cannot be selected for chat or delegate to other agents.</p>
        <LoadingBar active={items.loading || catalog.loading} />
        {items.error ? <ErrorBanner message={items.error} onRetry={items.reload} /> : null}
        {catalog.error ? <ErrorBanner message={catalog.error} onRetry={catalog.reload} /> : null}
        {error && !removing && !editing ? <ErrorBanner message={error} /> : null}
          {items.data?.length === 0 ? <Empty title="No sub-agents" detail="Add a specialist with its own instructions and selected capabilities." /> : null}
          {items.data?.map(item => <article className="card" key={item.id}>
            <div className="card-head"><h2><Bot size={16} />{item.name}</h2><div className="row">
              <button onClick={() => edit(item)}><Pencil size={14} />{readOnly ? 'View' : 'Edit'}</button>
              {!readOnly ? <button className="danger" aria-label={`Delete ${item.name}`} title="Delete sub-agent" onClick={() => { setError(null); setRemoving(item) }}><Trash2 size={14} /></button> : null}
            </div></div>
            <div className="card-body stack"><p>{item.description}</p><div className="row"><span className="badge">{item.modelId}</span><span className="hint">{item.tools.length} tools · {item.skills.length} skills</span></div></div>
          </article>)}
      </div>
    <Modal open={editing !== null} title={editing === 'new' ? 'New sub-agent' : `${readOnly ? 'View' : 'Edit'} sub-agent${editing ? `: ${editing.name}` : ''}`} icon={<Bot size={18} />} className="agent-editor-modal"
      onClose={() => {
        if (!busy) {
          setEditing(null)
          setError(null)
        }
      }} footer={<>
        <button disabled={busy} onClick={() => { setEditing(null); setError(null) }}>{readOnly ? 'Close' : 'Cancel'}</button>
        {!readOnly ? <button className="primary" disabled={busy || catalog.loading || !!catalog.error || !form.name.trim() || !form.description.trim() || !form.modelId.trim() || !form.instructions.trim()}
          onClick={() => void save()}>{editing === 'new' ? <Plus size={14} /> : <Check size={14} />}{busy ? 'Saving...' : editing === 'new' ? 'Create sub-agent' : 'Save changes'}</button> : null}
      </>}>
      <div className="stack">
        <p className="hint">Invoked only by {parent.name}. All tabs are saved together.</p>
        <LoadingBar active={catalog.loading} />
        {catalog.error ? <ErrorBanner message={catalog.error} onRetry={catalog.reload} /> : null}
        {error ? <ErrorBanner message={error} /> : null}
        {editing ? editorContent : null}
      </div>
    </Modal>
    <Modal open={!!removing} title="Delete sub-agent" icon={<Trash2 size={18} />} onClose={() => {
      if (!busy) {
        setRemoving(null)
        setError(null)
      }
    }} footer={<><button disabled={busy} onClick={() => { setRemoving(null); setError(null) }}>Cancel</button>
      <button className="danger" disabled={busy} onClick={() => void remove()}><Trash2 size={14} />{busy ? 'Deleting...' : 'Delete sub-agent'}</button></>}>
      <p>Delete <strong>{removing?.name}</strong> from {parent.name}? It will no longer be available on new turns. A turn already running may finish using its loaded configuration.</p>
      {error ? <ErrorBanner message={error} /> : null}
    </Modal>
  </>
}
