import { useState } from 'react'
import { CheckCircle2, Download, Eye, History, Trash2 } from 'lucide-react'
import { agentSkillScopeKey, deleteAgentSkillVersion, downloadAgentSkill, listAgentSkillVersions, setActiveAgentSkillVersion } from '../api/agentSkillClient'
import type { AgentSkill, AgentSkillScope, AgentSkillVersion } from '../api/agentSkillClient'
import { useAsync } from '../lib/useAsync'
import { useToast } from './Toast'
import { Empty, ErrorBanner, LoadingBar, Modal } from './ui'

export default function AgentSkillHistory({ skill, scope, readOnly, onClose, onInspect, onBusyChange, onChanged }: {
  skill: AgentSkill; scope: AgentSkillScope; readOnly: boolean; onClose: () => void
  onInspect: (version: number) => void; onBusyChange: (busy: boolean) => void
  onChanged: () => void
}) {
  const versions = useAsync(signal => listAgentSkillVersions(scope, skill.id, signal), [skill.id, scope.agentId, scope.subAgentId])
  const [removing, setRemoving] = useState<AgentSkillVersion | null>(null)
  const [promoting, setPromoting] = useState<AgentSkillVersion | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const toast = useToast()
  const canManage = !readOnly && !skill.isDefault && skill.scope === agentSkillScopeKey(scope)
  const promote = async () => {
    if (!promoting || busy) {
      return
    }
    setBusy(true)
    onBusyChange(true)
    setError(null)
    try {
      await setActiveAgentSkillVersion(scope, skill.id, promoting.version)
      toast.success(`Version ${promoting.version} is now active.`)
      setPromoting(null)
      versions.reload()
      onChanged()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not switch skill version.')
    } finally {
      setBusy(false)
      onBusyChange(false)
    }
  }
  const download = async (version: number) => {
    setBusy(true)
    onBusyChange(true)
    try {
      const blob = await downloadAgentSkill({ ...scope, version }, skill.id)
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = `${skill.name}-v${version}.zip`
      document.body.appendChild(link)
      try {
        link.click()
      } finally {
        link.remove()
        window.setTimeout(() => URL.revokeObjectURL(url), 1000)
      }
    } catch (cause) {
      toast.error(cause instanceof Error ? cause.message : 'Download failed.')
    } finally {
      setBusy(false)
      onBusyChange(false)
    }
  }
  const remove = async () => {
    if (!removing || busy) {
      return
    }
    setBusy(true)
    onBusyChange(true)
    setError(null)
    try {
      await deleteAgentSkillVersion(scope, skill.id, removing.version)
      setRemoving(null)
      versions.reload()
      onChanged()
      toast.success('Skill version deleted.')
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Could not delete version.')
    } finally {
      setBusy(false)
      onBusyChange(false)
    }
  }
  return <>
    <Modal open title={`Version history: ${skill.name}`} icon={<History size={17} />} onClose={() => {
      if (!busy) {
        onClose()
      }
    }} footer={<button disabled={busy} onClick={onClose}>Close</button>}>
      <p className="hint">Agents use the active version. Older versions can be inspected or downloaded. Deleting an older version does not interrupt turns already using its cached files.</p>
      <LoadingBar active={versions.loading || busy} />
      {versions.error ? <ErrorBanner message={versions.error} onRetry={versions.reload} /> : null}
      {versions.data?.length === 0 ? <Empty title="No versions" /> : null}
      <div className="stack">
        {versions.data?.map(version => <article className="card" key={version.version}>
          <div className="card-head"><h2>Version {version.version} {version.isActive ? <span className="badge">Active</span> : null}</h2>
            <div className="row">
              <button disabled={busy} title="Browse version files" aria-label={`Inspect version ${version.version}`} onClick={() => onInspect(version.version)}><Eye size={14} /></button>
              <button disabled={busy} title="Download version Zip" aria-label={`Download version ${version.version}`} onClick={() => void download(version.version)}><Download size={14} /></button>
              {canManage && !version.isActive ? <button disabled={busy} title="Make this version active" aria-label={`Make version ${version.version} active`} onClick={() => {
                setError(null)
                setPromoting(version)
              }}><CheckCircle2 size={14} />Make active</button> : null}
              {canManage && !version.isActive ? <button className="danger" disabled={busy} title="Delete unused version" aria-label={`Delete version ${version.version}`} onClick={() => {
                setError(null)
                setRemoving(version)
              }}><Trash2 size={14} /></button> : null}
            </div>
          </div>
          <div className="card-body"><p className="hint">{new Date(version.createdAtUtc).toLocaleString()}</p><p>{version.description}</p></div>
        </article>)}
      </div>
    </Modal>
    <Modal open={promoting !== null} title="Switch skill version" icon={<CheckCircle2 size={17} />} onClose={() => {
      if (!busy) {
        setPromoting(null)
      }
    }} footer={<><button disabled={busy} onClick={() => setPromoting(null)}>Cancel</button><button className="primary" disabled={busy} onClick={() => void promote()}><CheckCircle2 size={14} />{busy ? 'Switching...' : 'Make active'}</button></>}>
      <p>Make version {promoting?.version} of <strong>{skill.name}</strong> active? New turns will use its instructions, scripts, and resources. Running turns keep their loaded version.</p>
      <p className="hint">The skill name and agent assignments stay unchanged. All retained versions remain available to switch back to.</p>
      {error ? <ErrorBanner message={error} /> : null}
    </Modal>
    <Modal open={removing !== null} title="Delete skill version" icon={<Trash2 size={17} />} onClose={() => {
      if (!busy) {
        setRemoving(null)
      }
    }} footer={<><button disabled={busy} onClick={() => setRemoving(null)}>Cancel</button><button className="danger" disabled={busy} onClick={() => void remove()}><Trash2 size={14} />Delete version</button></>}>
      <p>Permanently delete version {removing?.version} of <strong>{skill.name}</strong> and its stored files? The current version and agent assignments will remain unchanged.</p>
      {error ? <ErrorBanner message={error} /> : null}
    </Modal>
  </>
}
