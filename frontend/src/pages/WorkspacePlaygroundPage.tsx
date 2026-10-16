import { useEffect, useId, useRef, useState } from 'react'
import { Archive, ArchiveRestore, ArrowRight, ArrowUp, Copy, Download, File, Folder, FolderInput, FolderPlus, Pencil, Play, Plus, RefreshCw, Square, Terminal, Trash2, Type, Upload } from 'lucide-react'
import {
  archivePlaygroundFiles, changePlaygroundInstance, createPlaygroundInstance, downloadPlaygroundFile,
  listPlaygroundFiles, listPlaygroundInstances, managePlaygroundFile, writePlaygroundText,
  type PlaygroundFile, type PlaygroundFiles, type PlaygroundInstance,
  type PlaygroundListing, type WorkspacePlaygroundProvider,
} from '../api/workspacePlaygroundClient'
import { Modal } from '../components/Modal'
import { useToast } from '../components/Toast'
import { ErrorBanner } from '../components/ui'
import WorkspaceExecutionPanel, { scriptLanguage, type OpenTerminalRequest } from './WorkspaceExecutionPanel'
import './WorkspacePlaygroundPage.css'

type FileAction = 'mkdir' | 'new' | 'edit' | 'rename' | 'move' | 'copy' | 'delete' | 'zip' | 'unzip'
type Dialog = { kind: FileAction; paths: string[] } | { kind: 'stop-instance' | 'stop-sandbox' | 'delete-instance'; paths: string[] }
const parent = (path: string) => path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : ''
const name = (path: string) => path.split('/').at(-1) ?? path
const join = (folder: string, file: string) => folder && folder !== '.' ? `${folder}/${file}` : file

export default function WorkspacePlaygroundPage({ provider, active }: { provider: WorkspacePlaygroundProvider; active: boolean }) {
  const uid = useId()
  const toast = useToast()
  const [listing, setListing] = useState<PlaygroundListing | null>(null)
  const [selected, setSelected] = useState<PlaygroundInstance | null>(null)
  const [files, setFiles] = useState<PlaygroundFiles | null>(null)
  const [folder, setFolder] = useState('')
  const [pathInput, setPathInput] = useState('')
  const [checked, setChecked] = useState<string[]>([])
  const [busy, setBusy] = useState('')
  const [error, setError] = useState('')
  const [dialog, setDialog] = useState<Dialog | null>(null)
  const [destination, setDestination] = useState('')
  const [text, setText] = useState('')
  const [terminalRequest, setTerminalRequest] = useState<OpenTerminalRequest | null>(null)
  const operation = useRef<AbortController | null>(null)
  const initialized = useRef(false)
  const upload = useRef<HTMLInputElement | null>(null)
  const title = provider === 'dynamic-sessions' ? 'ACA Dynamic Sessions' : 'ACA Sandboxes'
  const canBrowse = selected?.canAccessFiles && selected.state.toLowerCase() === 'running'
  const single = checked.length === 1 ? files?.entries.find(file => file.path === checked[0]) : undefined

  useEffect(() => () => operation.current?.abort(), [])

  const run = async (label: string, task: (signal: AbortSignal) => Promise<void>) => {
    if (operation.current) {
      return
    }
    const controller = new AbortController()
    operation.current = controller
    setBusy(label)
    setError('')
    try {
      await task(controller.signal)
    } catch (cause) {
      setError(controller.signal.aborted ? 'Request cancelled. The remote operation may still finish. Refresh before retrying.'
        : cause instanceof Error ? cause.message : 'The workspace request failed.')
    } finally {
      operation.current = null
      setBusy('')
    }
  }

  const loadFiles = async (target: PlaygroundInstance, path: string, signal: AbortSignal) => {
    const response = await listPlaygroundFiles(provider, target.id, path, signal)
    setFiles(response)
    setFolder(response.path)
    setPathInput(response.path)
    setChecked([])
  }

  const refresh = async (signal: AbortSignal) => {
    const response = await listPlaygroundInstances(provider, signal)
    setListing(response)
    if (selected) {
      const current = response.instances.find(item => item.id === selected.id) ?? null
      setSelected(current)
      if (!current || current.state.toLowerCase() !== 'running') {
        setFiles(null)
        setChecked([])
      }
    }
  }

  useEffect(() => {
    if (active && !initialized.current) {
      initialized.current = true
      void run('Loading instances', refresh)
    }
  }, [active]) // Each mounted tab owns its inputs and requests; refresh is explicit after first activation.

  const selectInstance = (target: PlaygroundInstance) => void run('Opening instance', async signal => {
    setSelected(target)
    setFiles(null)
    setFolder('')
    setPathInput('')
    setChecked([])
    if (target.canAccessFiles && target.state.toLowerCase() === 'running') {
      await loadFiles(target, '', signal)
    }
  })

  const create = () => void run('Creating instance', async signal => {
    const created = await createPlaygroundInstance(provider, signal)
    setSelected(created)
    setFiles(null)
    setChecked([])
    setListing(current => current ? { ...current, instances: [...current.instances.filter(item => item.id !== created.id), created] } : current)
    toast.success('Instance created.')
    await loadFiles(created, '', signal)
  })

  const openDialog = (kind: FileAction) => {
    setError('')
    setText('')
    setDialog({ kind, paths: [...checked] })
    setDestination(kind === 'rename' || kind === 'move' || kind === 'copy' ? checked[0]
      : kind === 'zip' ? join(folder, 'archive.zip')
        : kind === 'unzip' ? join(folder, name(checked[0]).replace(/\.zip$/i, '')) : join(folder, ''))
  }

  const editFile = (file: PlaygroundFile) => {
    if (!selected) {
      return
    }
    void run('Reading text file', async signal => {
      if ((file.sizeBytes ?? 0) > 1_000_000) {
        throw new Error('Text editing is limited to 1 MB. Download this file instead.')
      }
      const blob = await downloadPlaygroundFile(provider, selected.id, file.path, signal)
      if (blob.size > 1_000_000) {
        throw new Error('Text editing is limited to 1 MB. Download this file instead.')
      }
      const content = new TextDecoder('utf-8', { fatal: true }).decode(await blob.arrayBuffer())
      if (content.includes('\0')) {
        throw new Error('This is a binary file. Download it instead of editing as text.')
      }
      setText(content)
      setDestination(file.path)
      setDialog({ kind: 'edit', paths: [file.path] })
    })
  }

  const download = (file: PlaygroundFile) => {
    if (!selected) {
      return
    }
    void run('Downloading file', async signal => {
      const blob = await downloadPlaygroundFile(provider, selected.id, file.path, signal)
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = name(file.path)
      link.click()
      window.setTimeout(() => URL.revokeObjectURL(url), 30_000)
    })
  }

  const uploadFiles = (values: File[]) => {
    if (!selected || values.length === 0) {
      return
    }
    void run('Uploading files', async signal => {
      if (values.some(file => file.size > 5 * 1024 * 1024)) {
        throw new Error('Each uploaded file must be 5 MB or smaller. ZIP files are supported.')
      }
      for (const file of values) {
        const content = await new Promise<string>((resolve, reject) => {
          const reader = new FileReader()
          reader.onload = () => resolve(String(reader.result).split(',')[1])
          reader.onerror = () => reject(new Error(`Could not read ${file.name}.`))
          reader.readAsDataURL(file)
        })
        await managePlaygroundFile(provider, selected.id, { operation: 'upload', path: join(folder, file.name), content }, signal)
      }
      toast.success(`${values.length} file(s) uploaded.`)
      await loadFiles(selected, folder, signal)
    })
  }

  const submitDialog = () => {
    if (!dialog || !selected) {
      return
    }
    void run('Applying change', async signal => {
      if (dialog.kind === 'stop-sandbox') {
        await changePlaygroundInstance(provider, selected.id, 'stop', signal)
        setSelected({ ...selected, state: 'Stopping' })
        setFiles(null)
        setChecked([])
        setDialog(null)
        toast.success('Stop requested. Refresh to check the sandbox state.')
        return
      }
      if (dialog.kind === 'stop-instance' || dialog.kind === 'delete-instance') {
        await changePlaygroundInstance(provider, selected.id, dialog.kind === 'stop-instance' ? 'stop' : 'delete', signal)
        setSelected(null)
        setFiles(null)
        setChecked([])
        setListing(current => current ? { ...current, instances: current.instances.filter(item => item.id !== selected.id) } : current)
        setDialog(null)
        toast.success('Lifecycle request accepted. Refresh to check the current state.')
        return
      }
      if (dialog.kind === 'zip' || dialog.kind === 'unzip') {
        await archivePlaygroundFiles(provider, selected.id, dialog.kind, dialog.paths, destination, signal)
      } else if (dialog.kind === 'new' || dialog.kind === 'edit') {
        await writePlaygroundText(provider, selected.id, destination, text, dialog.kind === 'edit', signal)
      } else if (dialog.kind === 'mkdir') {
        await managePlaygroundFile(provider, selected.id, { operation: 'mkdir', path: destination }, signal)
      } else {
        for (const path of dialog.paths) {
          await managePlaygroundFile(provider, selected.id, { operation: dialog.kind, path, destination }, signal)
        }
      }
      setDialog(null)
      toast.success('File operation completed.')
      await loadFiles(selected, folder, signal)
    })
  }

  const destructive = dialog && ['delete', 'stop-instance', 'stop-sandbox', 'delete-instance'].includes(dialog.kind)
  const dialogTitle = dialog ? ({ mkdir: 'Create folder', new: 'Create text file', edit: 'Edit text file', rename: 'Rename', move: 'Move', copy: 'Copy', delete: 'Delete files and folders', zip: 'Create ZIP archive', unzip: 'Extract ZIP archive', 'stop-instance': 'Stop session', 'stop-sandbox': 'Stop sandbox', 'delete-instance': 'Delete sandbox' })[dialog.kind] : ''

  return <section className="card workspace-playground">
    <div className="card-head"><h2><Terminal size={18} />{title} playground</h2><span className="muted workspace-playground-status" role="status" aria-live="polite">{busy || 'Ready'}</span></div>
    <div className="card-body stack">
      <div className="workspace-playground-intro muted">
        <p>Manage {provider === 'dynamic-sessions' ? 'sessions' : 'sandboxes'}, files, and scripts. Changes apply immediately, including to chat instances; Azure usage may incur charges.</p>
        {provider === 'dynamic-sessions' && <p>Session files disappear on stop or expiry. Download files you want to keep.</p>}
      </div>
      {error && !dialog && <ErrorBanner message={error} />}
      <fieldset disabled={!!busy} className="workspace-playground-controls">
        {listing && !listing.configured && <p role="status">{listing.message}</p>}
        {listing?.truncated && <p role="status">The instance list reached the page limit. Some instances are not shown.</p>}
        <div className="workspace-playground-layout">
          <aside className="card workspace-instance-list" aria-label={`${title} instances`}>
            <div className="card-head">
              <h2><Terminal size={15} aria-hidden="true" />Instances</h2>
              <button type="button" className="ghost icon-only" title="Refresh instances" aria-label="Refresh instances" onClick={() => void run('Refreshing instances', refresh)}><RefreshCw size={14} aria-hidden="true" /></button>
              <button type="button" disabled={!listing?.configured} title={`Create ${provider === 'dynamic-sessions' ? 'session' : 'sandbox'}`} onClick={create}><Plus size={14} aria-hidden="true" />New</button>
            </div>
            <div className="workspace-instance-items">
            <p className="workspace-instance-count muted">{listing?.instances.length ?? 0} instances</p>
            {listing?.configured && listing.instances.length === 0 && <p className="muted">No active instances. Create one to begin.</p>}
            {listing?.instances.map(item => <button type="button" key={item.id} className={`workspace-instance ${selected?.id === item.id ? 'selected' : ''}`} aria-pressed={selected?.id === item.id} onClick={() => selectInstance(item)}>
              <strong title={item.id}>{item.id}</strong><span className="workspace-state" data-running={item.state.toLowerCase() === 'running'}>{item.state}</span>
              {item.expiresAt && <small>Expires {new Date(item.expiresAt).toLocaleString()}</small>}
            </button>)}
            </div>
          </aside>
          <div className="stack workspace-instance-detail">
            {!selected ? <p className="workspace-empty">Select an instance to manage its files and execute scripts.</p> : <>
              <div className="stack workspace-instance-header">
                <h3 className="workspace-instance-id">{selected.id}</h3>
                <div className="row workspace-instance-actions"><span className="workspace-state" data-running={selected.state.toLowerCase() === 'running'}>{selected.state}</span>
                  {provider === 'sandboxes' && <button type="button" disabled={selected.state.toLowerCase() === 'running'} onClick={() => void run('Resuming sandbox', async signal => {
                    await changePlaygroundInstance(provider, selected.id, 'resume', signal)
                    const response = await listPlaygroundInstances(provider, signal)
                    setListing(response)
                    const current = response.instances.find(item => item.id === selected.id) ?? null
                    setSelected(current)
                    if (current?.canAccessFiles && current.state.toLowerCase() === 'running') {
                      await loadFiles(current, folder, signal)
                    }
                  })}><Play size={15} aria-hidden="true" />Resume</button>}
                  {provider === 'sandboxes' && <button type="button" disabled={selected.state.toLowerCase() !== 'running'} onClick={() => { setError(''); setDialog({ kind: 'stop-sandbox', paths: [] }) }}><Square size={15} aria-hidden="true" />Stop sandbox</button>}
                  <button type="button" onClick={() => { setError(''); setDialog({ kind: provider === 'dynamic-sessions' ? 'stop-instance' : 'delete-instance', paths: [] }) }}>{provider === 'dynamic-sessions' ? <Square size={15} aria-hidden="true" /> : <Trash2 size={15} aria-hidden="true" />}{provider === 'dynamic-sessions' ? 'Stop session' : 'Delete sandbox'}</button>
                </div>
                {!selected.canAccessFiles && <p role="status">File access and scripts require a SandboxHost binding in this application's snapshot storage.</p>}
                {selected.canAccessFiles && !canBrowse && <p role="status">Resume the sandbox, then refresh before accessing files or scripts.</p>}
              </div>
              <fieldset disabled={!canBrowse} className="workspace-playground-controls stack">
                <section className="stack" aria-label="Files and folders">
                  <h3>Files and folders</h3>
                  <div className="row workspace-path">
                    <button type="button" disabled={!folder} onClick={() => void run('Opening folder', signal => loadFiles(selected, parent(folder), signal))} aria-label="Parent folder"><ArrowUp size={15} /></button>
                    <label htmlFor={`${uid}-path`}>Folder</label>
                    <input id={`${uid}-path`} value={pathInput} placeholder="Workspace root" onChange={event => setPathInput(event.target.value)} onKeyDown={event => {
                      if (event.key === 'Enter') {
                        event.preventDefault()
                        void run('Opening folder', signal => loadFiles(selected, pathInput, signal))
                      }
                    }} />
                    <button type="button" onClick={() => void run('Opening folder', signal => loadFiles(selected, pathInput, signal))}><ArrowRight size={15} aria-hidden="true" />Go</button>
                    <button type="button" onClick={() => void run('Refreshing files', signal => loadFiles(selected, folder, signal))} aria-label="Refresh files"><RefreshCw size={15} /></button>
                  </div>
                  <div className="row workspace-file-toolbar">
                    <button type="button" onClick={() => setTerminalRequest({ id: crypto.randomUUID(), instanceId: selected.id, folder })}><Terminal size={15} aria-hidden="true" />Open terminal here</button>
                    <button type="button" disabled={!single || single.isDirectory || !scriptLanguage(single.path)} onClick={() => {
                      if (single && !single.isDirectory && scriptLanguage(single.path)) {
                        setTerminalRequest({ id: crypto.randomUUID(), instanceId: selected.id, folder, scriptPath: single.path })
                      }
                    }}><Play size={15} aria-hidden="true" />Execute script</button>
                    <button type="button" onClick={() => openDialog('mkdir')}><FolderPlus size={15} />New folder</button>
                    <button type="button" onClick={() => openDialog('new')}><Plus size={15} />New file</button>
                    <button type="button" onClick={() => upload.current?.click()}><Upload size={15} />Upload</button>
                    <input ref={upload} type="file" multiple hidden onChange={event => { uploadFiles(Array.from(event.target.files ?? [])); event.target.value = '' }} />
                    <button type="button" disabled={!single || single.isDirectory} onClick={() => {
                      if (single) {
                        download(single)
                      }
                    }}><Download size={15} />Download</button>
                    <button type="button" disabled={!single || single.isDirectory} onClick={() => {
                      if (single) {
                        editFile(single)
                      }
                    }}><Pencil size={15} aria-hidden="true" />Edit text</button>
                    <button type="button" disabled={!single} onClick={() => openDialog('rename')}><Type size={15} aria-hidden="true" />Rename</button>
                    <button type="button" disabled={!single} onClick={() => openDialog('move')}><FolderInput size={15} aria-hidden="true" />Move</button>
                    <button type="button" disabled={!single} onClick={() => openDialog('copy')}><Copy size={15} aria-hidden="true" />Copy</button>
                    <button type="button" disabled={checked.length === 0} onClick={() => openDialog('delete')}><Trash2 size={15} aria-hidden="true" />Delete</button>
                    <button type="button" disabled={checked.length === 0} onClick={() => openDialog('zip')}><Archive size={15} />ZIP selected</button>
                    <button type="button" disabled={!single || single.isDirectory || !single.path.toLowerCase().endsWith('.zip')} onClick={() => openDialog('unzip')}><ArchiveRestore size={15} aria-hidden="true" />Unzip</button>
                  </div>
                  <p className="muted">Upload up to 5 MB per file. Select files or folders to create a ZIP, then download it. Paths are relative to the workspace root.</p>
                  <div className="workspace-file-table">
                    <table><thead><tr><th><input type="checkbox" aria-label="Select all files" checked={!!files?.entries.length && checked.length === files.entries.length} onChange={event => setChecked(event.target.checked ? files?.entries.map(file => file.path) ?? [] : [])} /></th><th>Name</th><th>Size</th><th>Modified</th></tr></thead>
                      <tbody>{files?.entries.map(file => <tr key={file.path}>
                        <td><input type="checkbox" aria-label={`Select ${name(file.path)}`} checked={checked.includes(file.path)} onChange={event => setChecked(current => event.target.checked ? [...current, file.path] : current.filter(path => path !== file.path))} /></td>
                        <td>{file.isDirectory ? <button type="button" className="ghost" onClick={() => void run('Opening folder', signal => loadFiles(selected, file.path, signal))}><Folder size={15} />{name(file.path)}</button> : <span className="row"><File size={15} />{name(file.path)}</span>}</td>
                        <td>{file.isDirectory ? 'Folder' : `${(file.sizeBytes ?? 0).toLocaleString()} B`}</td><td>{new Date(file.modifiedUtc).toLocaleString()}</td>
                      </tr>)}</tbody>
                    </table>
                    {files && files.entries.length === 0 && <p className="muted">This folder is empty.</p>}
                    {files?.truncated && <p role="status">This folder listing is truncated. Open a subfolder to see more files.</p>}
                  </div>
                </section>

              </fieldset>
            </>}
          </div>
        </div>
      </fieldset>
      {selected && terminalRequest?.instanceId === selected.id && <WorkspaceExecutionPanel key={terminalRequest.id} provider={provider} instanceId={selected.id}
        enabled={!!canBrowse && !busy} request={terminalRequest} onClose={() => setTerminalRequest(null)} />}
    </div>
    <Modal open={!!dialog} title={dialogTitle} className="workspace-file-dialog" onClose={() => {
      if (!busy) {
        setDialog(null)
      }
    }}
      footer={<><button type="button" disabled={!!busy} onClick={() => setDialog(null)}>Cancel</button><button type="button" className="primary" disabled={!!busy || (!destructive && !destination.trim())} onClick={submitDialog}>{busy ? 'Working…' : destructive ? 'Confirm' : 'Save'}</button></>}>
      <div className="stack">
        {error && <ErrorBanner message={error} />}
        <p className="workspace-instance-id">Instance: {selected?.id}</p>
        {dialog?.paths.map(path => <code key={path}>{path}</code>)}
        {destructive ? <p>{dialog?.kind === 'stop-instance' ? 'Stopping destroys the session and its temporary files. Chats using this session may be interrupted.'
          : dialog?.kind === 'stop-sandbox' ? 'Stop compute while retaining the sandbox disk. Running scripts and chats may be interrupted. Resume it before accessing files again.'
            : dialog?.kind === 'delete-instance' ? 'Deleting removes this sandbox and its files. Chats using it may be interrupted.' : 'Delete the selected files and folders, including all folder contents? This cannot be undone.'}</p>
          : <label>Destination path<input value={destination} disabled={!!busy || dialog?.kind === 'edit'} autoFocus onChange={event => setDestination(event.target.value)} /></label>}
        {(dialog?.kind === 'edit' || dialog?.kind === 'new') && <label>Text content<textarea className="workspace-code" spellCheck={false} value={text} maxLength={1_000_000} disabled={!!busy} onChange={event => setText(event.target.value)} /></label>}
        {dialog?.kind === 'edit' && <p className="muted">Saving replaces this file's current content.</p>}
        {(dialog?.kind === 'zip' || dialog?.kind === 'unzip') && <p className="muted">Existing files will not be overwritten. {dialog.kind === 'zip' ? 'Use a destination ending in .zip, outside any selected source folder.' : 'Use a destination folder for the extracted files.'}</p>}
      </div>
    </Modal>
  </section>
}
