import { useEffect, useId, useRef, useState, type DragEvent } from 'react'
import { Copy, FolderPlus, MoreHorizontal, Move, Pencil, Trash2, Upload, X, Folder, ArrowUp, ChevronRight } from 'lucide-react'
import { listConversationFiles, manageSandboxFile, uploadSandboxFile, type SandboxOperation } from '../api/client'
import type { FileSystemEntry } from '../api/types'
import { ErrorBanner, Modal } from './ui'
import { useAsync } from '../lib/useAsync'

type Operation = Exclude<SandboxOperation, 'upload'>
const labels: Record<Operation, string> = { mkdir: 'Create folder', rename: 'Rename', delete: 'Delete', copy: 'Copy', move: 'Move' }
const icons = { mkdir: FolderPlus, rename: Pencil, delete: Trash2, copy: Copy, move: Move }
const join = (parent: string, name: string) => parent === '.' ? name : `${parent}/${name}`
const leaf = (path: string) => path.split('/').pop() || path
const parent = (path: string) => path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '.'
const errorText = (error: unknown) => error instanceof Error ? error.message : String(error)

export function useSandboxFileManagement(conversationId: string, path: string, enabled: boolean, reload: () => void) {
  const [action, setAction] = useState<{ operation: Operation; entry?: FileSystemEntry } | null>(null)
  const [value, setValue] = useState('')
  const [folderPath, setFolderPath] = useState('.')
  const [destinationFolder, setDestinationFolder] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const busyRef = useRef(false)
  const [error, setError] = useState('')
  const [notice, setNotice] = useState('')
  const [progress, setProgress] = useState('')
  const [dialogError, setDialogError] = useState('')
  const input = useRef<HTMLInputElement>(null)
  const [dragging, setDragging] = useState(false)
  const dragDepth = useRef(0)
  const canDrop = enabled && !busy && !action

  function clearDrag() {
    dragDepth.current = 0
    setDragging(false)
  }

  useEffect(() => {
    clearDrag()
  }, [canDrop, path, conversationId])

  useEffect(() => {
    if (!dragging) {
      return
    }
    const cancel = (event: KeyboardEvent) => {
      if (event.key === 'Escape') {
        clearDrag()
      }
    }
    window.addEventListener('dragend', clearDrag)
    window.addEventListener('drop', clearDrag)
    window.addEventListener('keydown', cancel)
    return () => {
      window.removeEventListener('dragend', clearDrag)
      window.removeEventListener('drop', clearDrag)
      window.removeEventListener('keydown', cancel)
    }
  }, [dragging])

  function open(operation: Operation, entry?: FileSystemEntry) {
    setAction({ operation, entry })
    setValue(operation === 'mkdir' ? '' : leaf(entry!.path))
    setFolderPath(entry ? parent(entry.path) : path)
    setDestinationFolder(null)
    setDialogError('')
  }

  async function submit() {
    if (!action || busyRef.current || !enabled || ((action.operation === 'copy' || action.operation === 'move') && destinationFolder === null)) {
      return
    }
    busyRef.current = true
    setBusy(true)
    setDialogError('')
    try {
      const source = action.operation === 'mkdir' ? join(path, value) : action.entry!.path
      const destination = action.operation === 'rename' ? join(parent(source), value) : join(destinationFolder ?? '.', value)
      await manageSandboxFile(conversationId, action.operation, source, destination)
      setNotice(`${labels[action.operation]} completed.`)
      setError('')
      setAction(null)
      reload()
    } catch (cause) {
      setDialogError(errorText(cause))
    } finally {
      busyRef.current = false
      setBusy(false)
    }
  }

  async function upload(files: File[]) {
    if (!enabled || busyRef.current || files.length === 0) {
      return
    }
    busyRef.current = true
    setBusy(true)
    setError('')
    setNotice('')
    const failures: string[] = []
    let succeeded = 0
    try {
      for (const [index, file] of files.entries()) {
        setProgress(`Uploading ${index + 1} of ${files.length}: ${file.name}`)
        try {
          await uploadSandboxFile(conversationId, join(path, file.name), file)
          succeeded++
        } catch (cause) {
          failures.push(`${file.name}: ${errorText(cause)}`)
        }
      }
      setNotice(`${succeeded} of ${files.length} files uploaded.`)
      setError(failures.join('\n'))
    } finally {
      setProgress('')
      busyRef.current = false
      setBusy(false)
      reload()
    }
  }

  function drop(event: DragEvent) {
    event.preventDefault()
    clearDrag()
    if (!canDrop || busyRef.current) {
      return
    }
    if (Array.from(event.dataTransfer.items).some(item => item.webkitGetAsEntry?.()?.isDirectory)) {
      setError('Drop files only. Use New folder to create directories.')
      return
    }
    void upload(Array.from(event.dataTransfer.files))
  }

  const Icon = action ? icons[action.operation] : FolderPlus
  const transfer = action?.operation === 'copy' || action?.operation === 'move'
  const valid = value.length > 0 && value === value.trim() && !/[\\/:*?"<>|\u0000-\u001f]/.test(value) && !value.endsWith('.')
    && (!transfer || (destinationFolder !== null && join(destinationFolder, value) !== action?.entry?.path))

  return {
    busy,
    dragging: dragging && canDrop,
    dropHandlers: {
      onDrop: drop,
      onDragEnter: (event: DragEvent) => {
        event.preventDefault()
        if (canDrop && event.dataTransfer.types.includes('Files')) {
          dragDepth.current++
          setDragging(true)
        }
      },
      onDragOver: (event: DragEvent) => {
        event.preventDefault()
        event.dataTransfer.dropEffect = canDrop && event.dataTransfer.types.includes('Files') ? 'copy' : 'none'
      },
      onDragLeave: () => {
        dragDepth.current = Math.max(0, dragDepth.current - 1)
        if (dragDepth.current === 0) {
          setDragging(false)
        }
      },
    },
    toolbar: <>
      {enabled && <div className="sandbox-management-toolbar">
        <button disabled={busy} onClick={() => open('mkdir')}><FolderPlus size={15} />New folder</button>
        <button disabled={busy} onClick={() => input.current?.click()}><Upload size={15} />Upload files</button>
        <span className="hint">Drop files here · Up to 5 MB each · Changes affect everyone sharing this sandbox</span>
        <input type="file" hidden multiple ref={input} onChange={event => {
          void upload(Array.from(event.target.files ?? []))
          event.target.value = ''
        }} />
      </div>}
      {error && <ErrorBanner message={error} />}
      {(progress || notice) && <p className="hint" role="status">{progress || notice}</p>}
    </>,
    actions: (entry: FileSystemEntry) => enabled ? <SandboxActionMenu entry={entry} disabled={busy} onSelect={operation => open(operation, entry)} /> : null,
    dialog: <Modal open={!!action} title={action ? labels[action.operation] : 'Manage file'} icon={<Icon size={17} />}
      onClose={() => {
        if (!busy) {
          setAction(null)
        }
      }} footer={<>
        <button disabled={busy} onClick={() => setAction(null)}><X size={15} />Cancel</button>
        <button className={action?.operation === 'delete' ? 'danger' : 'primary'} disabled={busy || !enabled || (action?.operation !== 'delete' && !valid)} onClick={() => void submit()}>
          <Icon size={15} />{busy ? 'Working…' : action ? labels[action.operation] : 'Apply'}
        </button>
      </>}>
      {dialogError && <ErrorBanner message={dialogError} />}
      {action?.entry && <p className="mono">{action.entry.path}</p>}
      {action?.operation === 'delete' ? <p>Delete <strong>{action.entry?.path}</strong>{action.entry?.isDirectory ? ' and everything inside it' : ''}? Sandbox deletion is permanent.</p>
        : <label className="browse-field sandbox-management-field">Name
          <input type="text" autoFocus value={value} disabled={busy} onChange={event => setValue(event.target.value)} />
        </label>}
      {transfer && action?.entry && <div className="sandbox-destination">
        <h3>Destination folder</h3>
        <SandboxFolderPicker key={`${conversationId}:${folderPath}:${action.entry.path}`} conversationId={conversationId}
          path={folderPath} source={action.entry} disabled={busy} onReady={setDestinationFolder}
          onNavigate={next => {
            setDestinationFolder(null)
            setFolderPath(next)
          }} />
        <p className="hint">{destinationFolder !== null ? `Destination: ${join(destinationFolder, value)}` : 'Choose a destination folder.'} Existing items are never replaced.</p>
        {destinationFolder !== null && join(destinationFolder, value) === action.entry.path && <p className="hint">Choose another folder or change the name.</p>}
      </div>}
    </Modal>,
  }
}

function SandboxFolderPicker({ conversationId, path, source, disabled, onReady, onNavigate }: {
  conversationId: string
  path: string
  source: FileSystemEntry
  disabled: boolean
  onReady: (path: string | null) => void
  onNavigate: (path: string) => void
}) {
  const listing = useAsync(signal => listConversationFiles(conversationId, path, false, signal), [conversationId, path])
  const [address, setAddress] = useState(path)
  const current = listing.data?.path ?? path
  const insideSource = (candidate: string) => source.isDirectory && (candidate === source.path || candidate.startsWith(`${source.path}/`))
  const isFile = listing.data?.entries.some(entry => entry.path === current && !entry.isDirectory) ?? false
  const invalid = insideSource(current) || isFile
  const available = !listing.loading && !listing.error && !!listing.data?.sandboxStarted && !invalid

  useEffect(() => {
    onReady(available ? current : null)
  }, [available, current, onReady])

  const parts = current === '.' ? [] : current.split('/')
  const folders = (listing.data?.entries ?? []).filter(entry => entry.isDirectory)
    .sort((left, right) => leaf(left.path).localeCompare(leaf(right.path)))

  return <div className="sandbox-folder-picker" aria-busy={listing.loading}>
    <nav className="sandbox-picker-crumbs" aria-label="Destination folder path">
      <button type="button" className="ghost icon-only" aria-label="Up one destination folder" disabled={disabled || current === '.' || listing.loading} onClick={() => onNavigate(parent(current))}><ArrowUp size={15} /></button>
      <button type="button" className="ghost" disabled={disabled || current === '.'} onClick={() => onNavigate('.')}>Working directory</button>
      {parts.map((name, index) => <span key={index}><ChevronRight size={13} />
        <button type="button" className="ghost" disabled={disabled || index === parts.length - 1} onClick={() => onNavigate(parts.slice(0, index + 1).join('/'))}>{name}</button>
      </span>)}
    </nav>
    <form className="sandbox-address" onSubmit={event => {
      event.preventDefault()
      const next = address.trim().replace(/\\/g, '/').replace(/\/$/, '') || '.'
      if (next !== path) {
        onNavigate(next)
      }
    }}>
      <input type="text" aria-label="Destination folder address" value={address} disabled={disabled} onChange={event => setAddress(event.target.value)} />
      <button type="submit" disabled={disabled || listing.loading}>Go</button>
    </form>
    {listing.loading && <p role="status" className="hint">Loading folders…</p>}
    {listing.error && <ErrorBanner message={listing.error} onRetry={listing.reload} />}
    {!listing.loading && !listing.error && !listing.data?.sandboxStarted && <p>No sandbox is available. Send a question first.</p>}
    {!listing.loading && !listing.error && listing.data?.sandboxStarted && <>
      <div className="sandbox-picker-folders">
        {folders.map(folder => <button type="button" key={folder.path} disabled={disabled || insideSource(folder.path)}
          title={insideSource(folder.path) ? 'A folder cannot be copied or moved into itself.' : folder.path}
          onClick={() => onNavigate(folder.path)}><Folder size={16} /><span>{leaf(folder.path)}</span><ChevronRight size={14} /></button>)}
        {folders.length === 0 && !invalid && <p className="hint">No subfolders. You can use this folder.</p>}
      </div>
      {invalid && <p role="alert">{isFile ? 'This address points to a file. Choose a folder.' : 'A folder cannot be copied or moved into itself or its subfolders.'}</p>}
      {listing.data.truncated && <p className="hint">Only the first 500 entries are listed. Enter a folder address to reach folders not shown.</p>}
    </>}
  </div>
}

function SandboxActionMenu({ entry, disabled, onSelect }: {
  entry: FileSystemEntry
  disabled: boolean
  onSelect: (operation: Operation) => void
}) {
  const id = useId()
  const menu = useRef<HTMLDivElement>(null)
  const trigger = useRef<HTMLButtonElement>(null)
  const [expanded, setExpanded] = useState(false)

  function showMenu(last = false) {
    const popup = menu.current
    const button = trigger.current
    if (!popup || !button || disabled) {
      return
    }
    popup.showPopover()
    const rect = button.getBoundingClientRect()
    popup.style.left = `${Math.max(8, Math.min(rect.right - popup.offsetWidth, window.innerWidth - popup.offsetWidth - 8))}px`
    popup.style.top = `${Math.max(8, rect.bottom + popup.offsetHeight + 8 > window.innerHeight ? rect.top - popup.offsetHeight - 4 : rect.bottom + 4)}px`
    const buttons = popup.querySelectorAll<HTMLButtonElement>('[role="menuitem"]')
    buttons[last ? buttons.length - 1 : 0]?.focus()
  }

  return <>
    <button ref={trigger} type="button" className="ghost icon-only" disabled={disabled}
      title={`Manage ${leaf(entry.path)}`} aria-label={`Manage ${leaf(entry.path)}`}
      aria-haspopup="menu" aria-expanded={expanded} aria-controls={id}
      onClick={() => {
        if (menu.current?.matches(':popover-open')) {
          menu.current.hidePopover()
        } else {
          showMenu()
        }
      }} onKeyDown={event => {
        if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
          event.preventDefault()
          showMenu(event.key === 'ArrowUp')
        }
      }}><MoreHorizontal size={15} /></button>
    <div ref={menu} id={id} popover="auto" role="menu" aria-label={`Actions for ${leaf(entry.path)}`}
      className="sandbox-item-menu" onToggle={event => setExpanded(event.newState === 'open')}
      onKeyDown={event => {
        if (event.key === 'Escape') {
          event.preventDefault()
          event.stopPropagation()
          menu.current?.hidePopover()
          trigger.current?.focus()
          return
        }
        if (event.key === 'Tab') {
          menu.current?.hidePopover()
          trigger.current?.focus()
          return
        }
        if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(event.key)) {
          event.preventDefault()
          const buttons = Array.from(event.currentTarget.querySelectorAll<HTMLButtonElement>('[role="menuitem"]'))
          const index = buttons.indexOf(document.activeElement as HTMLButtonElement)
          const next = event.key === 'Home' ? 0 : event.key === 'End' ? buttons.length - 1
            : (index + (event.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length
          buttons[next]?.focus()
        }
      }}>
      {(['rename', 'copy', 'move', 'delete'] as const).map(operation => {
        const Icon = icons[operation]
        return <button key={operation} type="button" role="menuitem" tabIndex={-1} disabled={disabled}
          className={operation === 'delete' ? 'sandbox-menu-delete' : undefined}
          onClick={() => {
            menu.current?.hidePopover()
            trigger.current?.focus()
            onSelect(operation)
          }}><Icon size={15} aria-hidden="true" />{labels[operation]}</button>
      })}
    </div>
  </>
}
