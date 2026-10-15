import { Link } from 'react-router-dom'
import AgentSkillManagementButton from '../components/AgentSkillManagementButton'
import { useToast } from '../components/Toast'
import { useState } from 'react'
import { Bot, Check, Cloud, Eye, Pencil, Plus, RefreshCw, RotateCcw, Server, X, Wrench, BookOpen, FileText } from 'lucide-react'
import {
  createAgent,
  getDefaultAgentInstructions,
  getAgentCapabilities,
  listAgents,
  updateAgent,
} from '../api/client'
import type { AgentDefinition } from '../api/types'
import { CopyButton, Empty, ErrorBanner, Field, LoadingBar, Modal } from '../components/ui'
import { formatDateTime, formatRelative } from '../lib/format'
import { useAsync } from '../lib/useAsync'
import SubAgentManager from '../components/SubAgentManager'
import CapabilitySelector from '../components/CapabilitySelector'

type EditorTarget = AgentDefinition | 'new' | null

export default function AgentsPage() {
  const readOnly = !canManageAdministration(useAppUser())
  const agents = useAsync((signal) => listAgents(signal), [])
  const defaults = useAsync((signal) => getDefaultAgentInstructions(signal), [])
  const [editorTab, setEditorTab] = useState<'general' | 'tools' | 'skills' | 'sub-agents'>('general')
  const [editorFooterTarget, setEditorFooterTarget] = useState<HTMLDivElement | null>(null)
  const [editorSubAgentBusy, setEditorSubAgentBusy] = useState(false)
  const [editorSubAgentCount, setEditorSubAgentCount] = useState<number | null>(null)
  const [selectedTools, setSelectedTools] = useState<string[] | null>(null)
  const [selectedSkills, setSelectedSkills] = useState<string[] | null>(null)
  const [viewing, setViewing] = useState<AgentDefinition | null>(null)
  const [editing, setEditing] = useState<EditorTarget>(null)
  const editingId = editing && editing !== 'new' ? editing.id : undefined
  const capabilities = useAsync(signal => getAgentCapabilities(signal, editingId), [editingId])
  const [name, setName] = useState('')
  const [modelId, setModelId] = useState('')
  const [instructions, setInstructions] = useState('')
  const [busy, setBusy] = useState(false)
  const [dialogError, setDialogError] = useState<string | null>(null)
  const toast = useToast()
  const editingDefault =
    editing !== null && editing !== 'new' && editing.name.toLowerCase() === 'default'

  const openCreate = () => {
    setEditorSubAgentCount(null)
    setEditorTab('general')
    setSelectedTools(capabilities.data?.tools.map(item => item.name) ?? [])
    setSelectedSkills(capabilities.data?.skills.map(item => item.name) ?? [])
    setName('')
    setModelId(defaults.data?.modelId ?? '')
    setInstructions(defaults.data?.instructions ?? '')
    setDialogError(null)
    setEditing('new')
  }

  const openEdit = (agent: AgentDefinition) => {
    setEditorSubAgentCount(null)
    setEditorTab('general')
    setSelectedTools(agent.tools ?? null)
    setSelectedSkills(agent.skills ?? null)
    setName(agent.name)
    setModelId(agent.modelId)
    setInstructions(agent.instructions)
    setDialogError(null)
    setEditing(agent)
  }

  const save = async () => {
    const trimmedName = name.trim()
    const trimmedModelId = modelId.trim()
    const trimmedInstructions = instructions.trim()
    if (!trimmedName || !trimmedModelId || !trimmedInstructions) {
      setDialogError('Name, model ID, and instructions are required.')
      return
    }

    setBusy(true)
    setDialogError(null)

    try {
      if (editing === 'new') {
        await createAgent(trimmedName, trimmedModelId, trimmedInstructions, selectedTools, selectedSkills)
        toast.success('Agent created.')
      } else if (editing) {
        await updateAgent(editing.id, trimmedName, trimmedModelId, trimmedInstructions, selectedTools, selectedSkills)
        toast.success('Agent updated.')
      }
      setEditing(null)
      agents.reload()
    } catch (cause) {
      setDialogError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="stack">
      <div className="page-head">
        <div>
          <h1>
            <Bot size={20} />
            Agents
          </h1>
          <p>Manage agent instructions, models, and specialist sub-agents.</p>
        </div>
        <div className="row" style={{ gap: 8 }}>
          <Link className="button-link" to="/agents/skills"><BookOpen size={14} />Global skills</Link>
          <button onClick={() => {
            agents.reload()
            defaults.reload()
          }}>
            <RefreshCw size={14} />
            Refresh
          </button>
          <button className="primary" disabled={readOnly || !defaults.data || !capabilities.data || capabilities.loading || !!capabilities.error} onClick={openCreate}>
            <Plus size={14} />
            New agent
          </button>
        </div>
      </div>

      <LoadingBar active={agents.loading || defaults.loading} />
      {agents.error ? <ErrorBanner message={agents.error} onRetry={agents.reload} /> : null}
      {defaults.error ? <ErrorBanner message={defaults.error} onRetry={defaults.reload} /> : null}
      {capabilities.error ? <ErrorBanner message={capabilities.error} onRetry={capabilities.reload} /> : null}
      {defaults.data ? (
        <section className="card agent-hosting-summary" aria-label="Agent hosting configuration">
          <div className="agent-hosting-mode">
            <span className="agent-hosting-icon" aria-hidden="true">
              {defaults.data.mode === 'Foundry' ? <Cloud size={20} /> : <Server size={20} />}
            </span>
            <div>
              <div className="agent-hosting-label">Agent mode · All agents</div>
              <strong>{defaults.data.mode}</strong>
              <div className="agent-hosting-description">
                {defaults.data.mode === 'Foundry' ? 'Hosted in Microsoft Foundry' : 'Runs within the API'}
              </div>
            </div>
          </div>
          <div className="agent-hosting-endpoint">
            <span className="agent-hosting-label">
              {defaults.data.mode === 'Foundry' ? 'Foundry endpoint' : 'Azure OpenAI endpoint'}
            </span>
            <div className="agent-hosting-endpoint-value">
              <code>{defaults.data.endpoint || 'Not configured'}</code>
              {defaults.data.endpoint ? <CopyButton value={defaults.data.endpoint} label="Copy endpoint" /> : null}
            </div>
          </div>
        </section>
      ) : null}


      {agents.data && agents.data.length > 0 ? (
        <div className="agent-grid">
          {agents.data.map((agent) => (
            <div className="card agent-card" key={agent.id}>
              <div className="card-head">
                <h2>
                  <Bot size={15} />
                  {agent.name}
                </h2>
                <span className="hint" title={formatDateTime(agent.updatedAtUtc)}>
                  Updated {formatRelative(agent.updatedAtUtc)}
                </span>
              </div>
              <div className="card-body stack" style={{ gap: 14 }}>
                <span className="badge agent-model-id" title="Model ID">
                  {agent.modelId}
                </span>
                <p className="agent-preview">{agent.instructions}</p>
                <div className="row" style={{ justifyContent: 'flex-end', gap: 8 }}>
                  <button onClick={() => setViewing(agent)}>
                    <Eye size={14} />
                    View
                  </button>
                  <button className="primary" disabled={readOnly} onClick={() => openEdit(agent)}>
                    <Pencil size={14} />
                    Edit
                  </button>
                </div>
              </div>
            </div>
          ))}
        </div>
      ) : agents.data && !agents.loading ? (
        <div className="card">
          <Empty
            title="No agents"
            icon={<Bot size={26} strokeWidth={1.5} />}
            detail="Create an agent from the built-in SharePoint assistant instructions."
          />
        </div>
      ) : null}

      {viewing ? <AgentDetails key={viewing.id} agent={viewing} onClose={() => setViewing(null)} /> : null}

      <Modal
        open={editing !== null}
        title={editing === 'new' ? 'New agent' : 'Edit agent'}
        icon={<Bot size={17} />}
        className={`agent-editor-modal${editorTab === 'tools' || editorTab === 'skills' ? ' agent-capabilities-modal' : ''}`}
        bodyClassName={editorTab === 'tools' || editorTab === 'skills' ? 'agent-capabilities-body' : undefined}
        onClose={() => {
          if (!busy && !editorSubAgentBusy) {
            setEditing(null)
          }
        }}
        footer={
          <>
            <div ref={setEditorFooterTarget} className="agent-details-actions" />
            <button disabled={busy || editorSubAgentBusy} onClick={() => setEditing(null)}>
              <X size={14} />
              Cancel
            </button>
            <button
              className="primary"
              disabled={busy || editorSubAgentBusy || capabilities.loading || !!capabilities.error || !name.trim() || !modelId.trim() || !instructions.trim()}
              onClick={() => void save()}
            >
              {editing === 'new' ? <Plus size={14} /> : <Check size={14} />}
              {busy ? 'Saving…' : editing === 'new' ? 'Create agent' : 'Save changes'}
            </button>
          </>
        }
      >
        <div className="agent-editor-form agent-capabilities-layout">
          {dialogError ? <ErrorBanner message={dialogError} /> : null}
          {capabilities.error ? <ErrorBanner message={capabilities.error} onRetry={capabilities.reload} /> : null}
          <div className="sub-agent-tabs" role="tablist" aria-label="Agent configuration">
            {(['general', 'tools', 'skills', 'sub-agents'] as const).map((tab, index, tabs) => <button key={tab} type="button" role="tab"
              id={`main-agent-edit-${tab}`} aria-controls={`main-agent-edit-panel-${tab}`} aria-selected={editorTab === tab}
              tabIndex={editorTab === tab ? 0 : -1} className={editorTab === tab ? 'primary' : 'ghost'} onClick={() => setEditorTab(tab)}
              onKeyDown={event => {
                let next: typeof tab
                if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
                  next = tabs[(index + (event.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length]
                } else if (event.key === 'Home') {
                  next = tabs[0]
                } else if (event.key === 'End') {
                  next = tabs[tabs.length - 1]
                } else {
                  return
                }
                event.preventDefault()
                setEditorTab(next)
                document.getElementById(`main-agent-edit-${next}`)?.focus()
              }}>
              {tab === 'general' || tab === 'sub-agents' ? <Bot size={14} /> : tab === 'tools' ? <Wrench size={14} /> : <BookOpen size={14} />}
              {tab === 'general' ? 'General' : tab === 'sub-agents' ? `Sub-agents${editorSubAgentCount !== null ? ` (${editorSubAgentCount})` : ''}` : `${tab === 'tools' ? 'Tools' : 'Skills'} (${(tab === 'tools' ? selectedTools : selectedSkills)?.length ?? capabilities.data?.[tab].length ?? 0})`}
            </button>)}
          </div>
          <div id="main-agent-edit-panel-general" role="tabpanel" aria-labelledby="main-agent-edit-general" hidden={editorTab !== 'general'}>
          <div className="agent-editor-form">

          <div className="agent-editor-intro">
            <span className="agent-editor-mark">
              <Bot size={20} />
            </span>
            <div>
              <strong>Define this agent&apos;s behavior</strong>
              <span>
                Choose the model it uses, then describe its role, tool rules, safety boundaries, and
                preferred response style.
              </span>
            </div>
          </div>

          <div className="agent-editor-name-row">
            <Field
              label="Agent name"
              help={
                editingDefault
                  ? 'The default agent name is fixed.'
                  : 'Must be unique. Maximum 100 characters.'
              }
            >
              <input
                autoFocus={!editingDefault}
                type="text"
                maxLength={100}
                value={name}
                disabled={editingDefault}
                onChange={(event) => setName(event.target.value)}
                placeholder="For example: Contract reviewer"
              />
            </Field>
            <Field
              label="Model ID"
              help="Azure OpenAI deployment/model ID used for this agent."
            >
              <input
                type="text"
                maxLength={200}
                value={modelId}
                onChange={(event) => setModelId(event.target.value)}
                placeholder="For example: gpt-5-mini"
              />
            </Field>
            <span className="badge accent">
              {editing === 'new' ? 'Built-in template' : 'Custom instructions'}
            </span>
          </div>

          <div className="agent-instructions-field">
            <div className="agent-instructions-label">
              <label htmlFor="agent-instructions">Instructions</label>
              <span>{instructions.length.toLocaleString()} characters</span>
            </div>
            <textarea
              id="agent-instructions"
              className="agent-instructions-editor"
              value={instructions}
              onChange={(event) => setInstructions(event.target.value)}
              spellCheck={false}
            />
            <div className="agent-editor-help">
              <span>Plain text and Markdown are supported. Changes take effect when you save.</span>
              {editing === 'new' && instructions !== defaults.data?.instructions ? (
                <button
                  className="ghost"
                  type="button"
                  onClick={() => setInstructions(defaults.data?.instructions ?? '')}
                >
                  <RotateCcw size={13} />
                  Restore template
                </button>
              ) : null}
            </div>
          </div>
          </div>
          </div>
          {(['tools', 'skills'] as const).map(kind => <div className="agent-capability-panel" key={kind} id={`main-agent-edit-panel-${kind}`} role="tabpanel" aria-labelledby={`main-agent-edit-${kind}`} hidden={editorTab !== kind}>
            {kind === 'skills' ? <AgentSkillManagementButton scope={{ agentId: editingId }} disabled={busy} onChanged={removed => {
              capabilities.reload()
              agents.reload()
              if (removed) {
                setSelectedSkills(current => current?.filter(name => name !== removed) ?? null)
              }
            }} /> : null}
            <CapabilitySelector kind={kind} items={capabilities.data?.[kind] ?? []} selected={kind === 'tools' ? selectedTools : selectedSkills}
              disabled={busy || capabilities.loading || !!capabilities.error} onChange={kind === 'tools' ? setSelectedTools : setSelectedSkills} />
          </div>)}
          <div id="main-agent-edit-panel-sub-agents" role="tabpanel" aria-labelledby="main-agent-edit-sub-agents" hidden={editorTab !== 'sub-agents'}>
            {editing && editing !== 'new' ? <div className="stack">
              <p className="hint">Sub-agent changes are saved immediately and independently of this agent's Save changes or Cancel actions.</p>
              <SubAgentManager key={editing.id} parent={editing} readOnly={readOnly || busy} active={editorTab === 'sub-agents'}
                footerTarget={editorFooterTarget} onBusyChange={setEditorSubAgentBusy} onCountChange={setEditorSubAgentCount} />
            </div> : <Empty title="Create the agent first" detail="After creating this agent, open Edit to add sub-agents." />}
          </div>
        </div>
      </Modal>
    </div>
  )
}
import { useAppUser, canManageAdministration } from '../components/AppUserContext'

function AgentDetails({ agent, onClose }: { agent: AgentDefinition; onClose: () => void }) {
  const [tab, setTab] = useState<'instructions' | 'tools' | 'skills' | 'sub-agents'>('instructions')
  const [footerTarget, setFooterTarget] = useState<HTMLDivElement | null>(null)
  const [subAgentBusy, setSubAgentBusy] = useState(false)
  const [subAgentCount, setSubAgentCount] = useState<number | null>(null)
  const catalog = useAsync(signal => getAgentCapabilities(signal, agent.id), [agent.id])
  const assigned = {
    tools: agent.tools?.map(name => catalog.data?.tools.find(item => item.name === name) ?? { name, description: 'Not available in this deployment.' }) ?? catalog.data?.tools ?? [],
    skills: agent.skills?.map(name => catalog.data?.skills.find(item => item.name === name) ?? { name, description: 'Not available in this deployment.' }) ?? catalog.data?.skills ?? [],
  }
  const tabs = ['instructions', 'tools', 'skills', 'sub-agents'] as const
  const close = () => {
    if (!subAgentBusy) {
      onClose()
    }
  }
  return <Modal open title={agent.name} icon={<Eye size={17} />} className={`agent-editor-modal${tab === 'tools' || tab === 'skills' ? ' agent-capabilities-modal' : ''}`} onClose={close}
    bodyClassName={tab === 'tools' || tab === 'skills' ? 'agent-capabilities-body' : undefined}
    footer={<>
      <div ref={setFooterTarget} className="agent-details-actions" />
      <button disabled={subAgentBusy} onClick={close}><X size={14} />Close</button>
    </>}>
    <div className="stack agent-capabilities-layout" style={{ gap: 12 }}>
    <span className="badge agent-model-id" title="Model ID">{agent.modelId}</span>
    <div className="row" role="tablist" aria-label="Agent details">
      {tabs.map((name, index) => <button
        key={name}
        id={`agent-tab-${name}`}
        role="tab"
        aria-selected={tab === name}
        aria-controls={name === 'sub-agents' ? 'agent-sub-agents-panel' : 'agent-details-panel'}
        tabIndex={tab === name ? 0 : -1}
        className={tab === name ? 'primary' : 'ghost'}
        onClick={() => setTab(name)}
        onKeyDown={event => {
          let next: typeof tab
          if (event.key === 'ArrowRight' || event.key === 'ArrowLeft') {
            next = tabs[(index + (event.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length]
          } else if (event.key === 'Home') {
            next = tabs[0]
          } else if (event.key === 'End') {
            next = tabs[tabs.length - 1]
          } else {
            return
          }
          event.preventDefault()
          setTab(next)
          document.getElementById(`agent-tab-${next}`)?.focus()
        }}
      >
        {name === 'instructions' ? <FileText size={14} /> : name === 'tools' ? <Wrench size={14} /> : name === 'skills' ? <BookOpen size={14} /> : <Bot size={14} />}
        {name === 'instructions' ? 'Instructions' : name === 'tools' ? 'Tools' : name === 'skills' ? 'Skills' : 'Sub-agents'}
        {(name === 'tools' || name === 'skills') && catalog.data ? ` (${assigned[name].length})` : ''}
        {name === 'sub-agents' && subAgentCount !== null ? ` (${subAgentCount})` : ''}
      </button>)}
    </div>
    {tab !== 'sub-agents' ? <div className={tab === 'tools' || tab === 'skills' ? 'agent-capability-panel' : undefined} id="agent-details-panel" role="tabpanel" aria-labelledby={`agent-tab-${tab}`} tabIndex={0}>
      {tab === 'instructions' ? <pre className="agent-instructions">{agent.instructions}</pre> : <div className="stack agent-capability-selector">
        <p className="hint">{agent[tab] == null ? 'This agent uses all available capabilities, including future additions.' : 'These are the capabilities selected for this agent. Edit the agent to change them.'} Skills use the shared database catalog and file storage. The API synchronizes bundled default skills at startup.</p>
        <LoadingBar active={catalog.loading} />
        {catalog.error ? <ErrorBanner message={catalog.error} onRetry={catalog.reload} /> : null}
        {tab === 'skills' ? <AgentSkillManagementButton scope={{ agentId: agent.id }} readOnly /> : null}
        <div className="agent-capability-list stack" tabIndex={0} aria-label={`Assigned ${tab}`}>
        {catalog.data && assigned[tab].length === 0 ? <Empty title={`No ${tab} selected`} /> : null}
        {assigned[tab].map(item => <article className="card agent-capability" key={item.name}>
          <strong>{item.name}</strong>
          <p>{item.description}</p>
        </article>)}
        </div>
      </div>}
    </div> : null}
    <div id="agent-sub-agents-panel" role="tabpanel" aria-labelledby="agent-tab-sub-agents" hidden={tab !== 'sub-agents'}>
      <SubAgentManager parent={agent} readOnly active={tab === 'sub-agents'} footerTarget={footerTarget} onBusyChange={setSubAgentBusy} onCountChange={setSubAgentCount} />
    </div>
  </div>
  </Modal>
}
