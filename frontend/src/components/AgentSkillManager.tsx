import { useEffect, useRef, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { BookOpen, Copy, Download, Eye, File, Folder, Trash2, Upload } from 'lucide-react'
import { agentSkillScopeKey, deleteAgentSkill, downloadAgentSkillFile, listAgentSkillFiles, listAgentSkills, readAgentSkillFile, uploadAgentSkill } from '../api/agentSkillClient'
import type { AgentSkill, AgentSkillScope } from '../api/agentSkillClient'
import { useAsync } from '../lib/useAsync'
import { useCopy } from '../lib/useCopy'
import { useToast } from './Toast'
import SourceCode from './SourceCode'
import { Empty, ErrorBanner, LoadingBar, Modal } from './ui'

export default function AgentSkillManager({ scope = {}, readOnly = false, onChanged, onBusyChange }: {
  scope?: AgentSkillScope; readOnly?: boolean; onChanged?: (removedName?: string) => void; onBusyChange?: (busy: boolean) => void
}) {
  const skills = useAsync(signal => listAgentSkills(scope, signal), [scope.agentId, scope.subAgentId])
  const [inspecting, setInspecting] = useState<AgentSkill | null>(null)
  const [removing, setRemoving] = useState<AgentSkill | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const input = useRef<HTMLInputElement>(null)
  const toast = useToast()
  useEffect(() => {
    onBusyChange?.(busy)
    return () => onBusyChange?.(false)
  }, [busy, onBusyChange])
  const changed = (removedName?: string) => {
    skills.reload()
    onChanged?.(removedName)
  }
  const upload = async (file?: File) => {
    if (!file) {
      return
    }
    if (!file.name.toLowerCase().endsWith('.zip') || file.size > 10 * 1024 * 1024) {
      setError('Select a ZIP file no larger than 10 MB.')
      return
    }
    setBusy(true)
    setError(null)
    try {
      await uploadAgentSkill(scope, file)
      toast.success('Skill uploaded.')
      changed()
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(false)
      if (input.current) {
        input.current.value = ''
      }
    }
  }
  const remove = async () => {
    if (!removing) {
      return
    }
    setBusy(true)
    setError(null)
    try {
      await deleteAgentSkill(scope, removing.id)
      setRemoving(null)
      toast.success('Skill deleted.')
      changed(removing.name)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setBusy(false)
    }
  }
  return <div className="agent-skill-manager stack">
    <div className="row" style={{ justifyContent: 'space-between' }}>
      <p className="hint">{scope.agentId ? 'Upload skills for this agent only. Global skills are managed from the Agents page.' : 'Global skills are available to all agents. Bundled default skills are read-only.'}</p>
      {!readOnly ? <button className="primary" disabled={busy} onClick={() => input.current?.click()}><Upload size={14} />{busy ? 'Working...' : 'Upload ZIP'}</button> : null}
      <input ref={input} type="file" accept=".zip,application/zip" hidden aria-label="Upload skill ZIP" onChange={event => void upload(event.target.files?.[0])} />
    </div>
    {!readOnly ? <p className="hint">One SKILL.md with name and description, plus optional scripts and resources. Maximum 10 MB ZIP / 20 MB extracted. Uploads and deletions take effect immediately.</p> : null}
    <LoadingBar active={skills.loading || busy} />
    {skills.error ? <ErrorBanner message={skills.error} onRetry={skills.reload} /> : null}
    {error && !removing ? <ErrorBanner message={error} /> : null}
    <div className="agent-skill-list stack">
      {skills.data?.length === 0 ? <Empty title="No skills" /> : null}
      {skills.data?.map(skill => <article className="card" key={skill.id}>
        <div className="card-head"><h2><BookOpen size={15} />{skill.name}</h2><div className="row">
          <span className="badge">{skill.isDefault ? 'Default' : skill.scope === 'global' ? 'Global' : 'Agent-specific'}</span>
          <button title="Browse skill files" aria-label={`Inspect ${skill.name}`} onClick={() => setInspecting(skill)}><Eye size={14} /></button>
          {!readOnly && !skill.isDefault && skill.scope === agentSkillScopeKey(scope) ? <button className="danger" title="Delete skill" aria-label={`Delete ${skill.name}`} disabled={busy} onClick={() => { setError(null); setRemoving(skill) }}><Trash2 size={14} /></button> : null}
        </div></div>
        <div className="card-body"><p>{skill.description}</p></div>
      </article>)}
    </div>
    {inspecting ? <AgentSkillInspector skill={inspecting} scope={scope} onClose={() => setInspecting(null)} /> : null}
    <Modal open={!!removing} title="Delete skill" icon={<Trash2 size={17} />} onClose={() => {
      if (!busy) {
        setRemoving(null)
      }
    }} footer={<><button disabled={busy} onClick={() => setRemoving(null)}>Cancel</button><button className="danger" disabled={busy} onClick={() => void remove()}><Trash2 size={14} />Delete skill</button></>}>
      <p>Delete <strong>{removing?.name}</strong>? It will be removed from saved agent selections. Running turns may finish using their existing copy.</p>
      {error ? <ErrorBanner message={error} /> : null}
    </Modal>
  </div>
}

function AgentSkillInspector({ skill, scope, onClose }: { skill: AgentSkill; scope: AgentSkillScope; onClose: () => void }) {
  const files = useAsync(signal => listAgentSkillFiles(scope, skill.id, signal), [skill.id, scope.agentId, scope.subAgentId])
  const [path, setPath] = useState('SKILL.md')
  const [downloading, setDownloading] = useState(false)
  const [renderMarkdown, setRenderMarkdown] = useState(false)
  const isMarkdown = /\.(md|markdown)$/i.test(path)
  const copy = useCopy()
  const toast = useToast()
  const preview = useAsync(signal => readAgentSkillFile(scope, skill.id, path, signal), [skill.id, scope.agentId, scope.subAgentId, path])
  const download = async () => {
    setDownloading(true)
    try {
      const content = !preview.loading && !preview.error ? preview.data?.content : null
      const blob = content != null
        ? new Blob([content], { type: 'text/plain;charset=utf-8' })
        : await downloadAgentSkillFile(scope, skill.id, path)
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = path.split('/').pop() ?? path
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
      setDownloading(false)
    }
  }
  return <Modal open title={skill.name} icon={<BookOpen size={17} />} className="agent-skill-inspector" onClose={onClose} footer={<button onClick={onClose}>Close</button>}>
    <LoadingBar active={files.loading || preview.loading} />
    {files.error ? <ErrorBanner message={files.error} onRetry={files.reload} /> : null}
    <div className="agent-skill-browser">
      <nav aria-label="Skill files">
        <strong><Folder size={14} /> Files</strong>
        {files.data?.map(file => <button key={file.path} className={path === file.path ? 'primary' : 'ghost'} aria-current={path === file.path ? 'page' : undefined}
          onClick={() => {
            setPath(file.path)
            setRenderMarkdown(false)
          }} title={`${file.path} (${file.size.toLocaleString()} bytes)`}><File size={14} /><span>{file.path}</span></button>)}
      </nav>
      <section aria-label="File preview">
        <div className="agent-skill-preview-header">
          <strong>{path}</strong>
          <div className="row">
            {isMarkdown ? <button aria-pressed={renderMarkdown} disabled={preview.loading || !!preview.error || preview.data?.content == null}
              onClick={() => setRenderMarkdown(value => !value)} title={renderMarkdown ? 'View Markdown source' : 'Preview Markdown'}>
              <Eye size={14} />{renderMarkdown ? 'Source' : 'Preview'}
            </button> : null}
            <button disabled={preview.loading || !!preview.error || preview.data?.content == null}
              onClick={() => void copy(preview.data?.content ?? '', 'File content')} title="Copy file content"><Copy size={14} />Copy</button>
            <button disabled={downloading || !files.data?.some(file => file.path === path)}
              onClick={() => void download()} title="Download file"><Download size={14} />{downloading ? 'Downloading...' : 'Download'}</button>
          </div>
        </div>
        {preview.error ? <ErrorBanner message={preview.error} onRetry={preview.reload} /> : null}
        {!preview.loading && preview.data ? preview.data.content !== null ? renderMarkdown && isMarkdown
          ? <div className="markdown agent-skill-markdown" tabIndex={0} aria-label={`${path} rendered Markdown`}>
            <ReactMarkdown remarkPlugins={[remarkGfm]} skipHtml>{preview.data.content}</ReactMarkdown>
          </div>
          : <SourceCode path={path} value={preview.data.content} /> : <p className="hint">{preview.data.message}</p> : null}
      </section>
    </div>
  </Modal>
}
