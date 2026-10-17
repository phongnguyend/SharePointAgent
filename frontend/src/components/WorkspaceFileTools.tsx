import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Archive, Plus, Terminal, Trash2 } from 'lucide-react'
import { downloadConversationFile, manageWorkspaceFile } from '../api/client'
import { request } from '../api/http'
import type { FileSystemEntry } from '../api/types'
import type { PlaygroundExecutionResult } from '../api/workspacePlaygroundClient'
import WorkspaceExecutionPanel, { scriptLanguage, type OpenTerminalRequest } from '../pages/WorkspaceExecutionPanel'
import { Modal } from './Modal'
import { ErrorBanner } from './ui'
import { useToast } from './Toast'
import '../pages/WorkspacePlaygroundPage.css'

type Action = 'new' | 'edit' | 'zip' | 'unzip' | 'delete'
const titles = { new: 'New file', edit: 'Edit text', zip: 'Zip selected', unzip: 'Unzip', delete: 'Delete selected' }

export function WorkspaceFileTools({ conversationId, path, entries, selected, setSelected, enabled, canExecute, reload, leadingActions, editRequest }: {
  conversationId: string; path: string; entries: FileSystemEntry[]; selected: string[]
  setSelected: (paths: string[]) => void; enabled: boolean; canExecute: boolean; reload: () => void
  leadingActions?: ReactNode
  editRequest?: { entry: FileSystemEntry; operation: 'edit' | 'unzip' | 'execute' } | null
}) {
  const toast = useToast()
  const [action, setAction] = useState<Action | null>(null)
  const [archivePath, setArchivePath] = useState('')
  const [destination, setDestination] = useState('')
  const [content, setContent] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const [terminal, setTerminal] = useState<OpenTerminalRequest | null>(null)
  const pending = useRef<AbortController | null>(null)
  const join = (name: string) => path === '.' || !path ? name : `${path}/${name}`
  const operate = <T,>(body: object, signal?: AbortSignal) => request<T>(`/api/chat/conversations/${encodeURIComponent(conversationId)}/files/operations`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body), signal,
  })
  useEffect(() => () => pending.current?.abort(), [])

  async function run(task: (signal: AbortSignal) => Promise<void>) {
    if (pending.current || !enabled) {
      return
    }
    const controller = new AbortController()
    pending.current = controller
    setBusy(true)
    setError('')
    try {
      await task(controller.signal)
    } catch (cause) {
      if (!controller.signal.aborted) {
        setError(cause instanceof Error ? cause.message : String(cause))
      }
    } finally {
      pending.current = null
      setBusy(false)
    }
  }

  useEffect(() => {
    if (editRequest && enabled && !pending.current) {
      if (editRequest.operation === 'execute') {
        if (canExecute && !editRequest.entry.isDirectory && scriptLanguage(editRequest.entry.path)) {
          openTerminal(editRequest.entry.path)
        }
      } else {
        open(editRequest.operation, editRequest.entry)
      }
    }
  }, [editRequest])

  function open(next: Action, editEntry?: FileSystemEntry) {
    setError('')
    setContent('')
    setDestination(next === 'zip' ? join('archive.zip') : next === 'unzip' ? (editEntry?.path ?? '').replace(/\.zip$/i, '') : join(''))
    setArchivePath(next === 'unzip' ? editEntry?.path ?? '' : '')
    setAction(next)
    if (next === 'edit' && editEntry && !editEntry.isDirectory) {
      setDestination(editEntry.path)
      void run(async signal => {
        if ((editEntry.sizeBytes ?? 0) > 1_000_000) {
          throw new Error('Text editing is limited to 1 MB.')
        }
        const blob = await downloadConversationFile(conversationId, editEntry.path, true, signal)
        if (blob.size > 1_000_000) {
          throw new Error('Text editing is limited to 1 MB.')
        }
        const text = new TextDecoder('utf-8', { fatal: true }).decode(await blob.arrayBuffer())
        if (text.includes('\0')) {
          throw new Error('This is a binary file; download it instead.')
        }
        setContent(text)
      })
    }
  }

  const save = () => void run(async signal => {
    try {
      if (action === 'delete') {
        for (const file of selected) {
          await manageWorkspaceFile(conversationId, 'delete', file)
        }
      } else {
        await operate({ operation: action === 'new' || action === 'edit' ? 'text' : action,
          path: destination, destination, paths: action === 'unzip' ? [archivePath] : selected, content, overwrite: action === 'edit' }, signal)
      }
      toast.success('Workspace files updated.')
      setAction(null)
      setSelected([])
    } finally {
      reload()
    }
  })
  const openTerminal = (scriptPath?: string) => setTerminal({ id: crypto.randomUUID(), instanceId: conversationId, folder: path, scriptPath })

  return <>
    <div className="workspace-files-management-toolbar">
      {leadingActions}
      <label><input type="checkbox" checked={entries.length > 0 && selected.length === entries.length} disabled={!enabled || busy || !entries.length}
        onChange={event => setSelected(event.target.checked ? entries.map(entry => entry.path) : [])} />Select all</label>
      <span className="hint">{selected.length} selected</span>
      <button disabled={!enabled || busy} onClick={() => open('new')}><Plus size={15} />New file</button>
      <button disabled={!enabled || busy || !selected.length || selected.length > 100} onClick={() => open('zip')}><Archive size={15} />Zip selected</button>
      <button disabled={!enabled || busy || !selected.length} onClick={() => open('delete')}><Trash2 size={15} />Delete selected</button>
      {canExecute && <>
        <button disabled={!enabled || busy} onClick={() => openTerminal()}><Terminal size={15} />Open terminal here</button>
      </>}
    </div>
    {error && !action && <ErrorBanner message={error} />}
    <Modal open={!!action} title={action ? titles[action] : 'Workspace files'} className="workspace-file-dialog" onClose={() => {
      if (!busy) {
        setAction(null)
      }
    }}
      footer={<><button disabled={busy} onClick={() => setAction(null)}>Cancel</button><button className={action === 'delete' ? 'danger' : 'primary'}
        disabled={!enabled || busy || !!error || (action !== 'delete' && !destination.trim())} onClick={save}>{busy ? 'Working…' : action === 'delete' ? 'Delete' : 'Save'}</button></>}>
      <div className="stack">
        {error && <ErrorBanner message={error} />}
        {action === 'delete' ? <p>Delete {selected.length} selected files and folders, including their contents? This cannot be undone.</p> : <>
          <label>Destination path<input disabled={busy || action === 'edit'} value={destination} onChange={event => { setDestination(event.target.value); setError('') }} /></label>
          {(action === 'new' || action === 'edit') && <label>Text content<textarea className="workspace-code" value={content} maxLength={1_000_000} disabled={busy}
            onChange={event => setContent(event.target.value)} spellCheck={false} /></label>}
          {(action === 'zip' || action === 'unzip') && <p className="hint">Existing files are never overwritten. Use a destination outside the selected source folders.</p>}
          {action === 'edit' && <p className="hint">Saving replaces the file's current content.</p>}
        </>}
      </div>
    </Modal>
    {terminal && <WorkspaceExecutionPanel key={terminal.id} provider="foundry-sessions" instanceId={conversationId} request={terminal} enabled={enabled && canExecute}
      executeRequest={(execution, signal) => operate<PlaygroundExecutionResult>({ operation: 'execute', execution }, signal)}
      onClose={() => { setTerminal(null); reload() }} />}
  </>
}
