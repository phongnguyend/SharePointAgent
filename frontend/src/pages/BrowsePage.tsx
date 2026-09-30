import { useRef, useState, type DragEvent } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Copy, ExternalLink, File, Folder, FolderPlus, Move, Pencil, RefreshCw, Trash2, Upload, X } from 'lucide-react'
import { browseSharePoint, changeSharePointItem, createSharePointFolder, uploadSharePointFile, type BrowseItem, type BrowseListing } from '../api/client'
import { canManageAdministration, useAppUser } from '../components/AppUserContext'
import { Empty, ErrorBanner, LoadingBar, Modal } from '../components/ui'
import { formatBytes, formatDateTime } from '../lib/format'
import { useAsync } from '../lib/useAsync'
import RecycleBinView from './RecycleBinView'

type Operation = 'new' | 'rename' | 'delete' | 'copy' | 'move'
type Action = { operation: Operation; item?: BrowseItem }
const titles: Record<Operation, string> = { new: 'Create folder', rename: 'Rename', delete: 'Delete', copy: 'Copy', move: 'Move' }
const actionIcons = { new: FolderPlus, rename: Pencil, delete: Trash2, copy: Copy, move: Move }
const message = (error: unknown) => error instanceof Error ? error.message : String(error)

export default function BrowsePage() {
  const [params, setParams] = useSearchParams()
  const folderId = params.get('folder') || 'root'
  const recycleBin = params.get('view') === 'recycle-bin'
  function switchView(recycle: boolean) {
    const next = new URLSearchParams(params)
    if (recycle) {
      next.set('view', 'recycle-bin')
    } else {
      next.delete('view')
    }
    setParams(next)
  }
  return <div className="browse-page">
    <nav className="browse-tools" aria-label="Browse views">
      <button className={recycleBin ? '' : 'primary'} aria-pressed={!recycleBin} onClick={() => switchView(false)}><Folder size={16} />Documents</button>
      <button className={recycleBin ? 'primary' : ''} aria-pressed={recycleBin} onClick={() => switchView(true)}><Trash2 size={16} />Recycle bin</button>
    </nav>
    {recycleBin ? <RecycleBinView /> : <FolderView key={folderId} folderId={folderId} navigate={(id) => setParams(id === 'root' ? {} : { folder: id })} />}
  </div>
}

function Breadcrumbs({ listing, navigate, disabled }: { listing: BrowseListing; navigate: (id: string) => void; disabled: boolean }) {
  return <nav className="browse-breadcrumbs" aria-label="Folder path">
    {listing.breadcrumbs.map((item, index) => <span key={item.id}>
      {index > 0 && <span aria-hidden="true"> / </span>}
      <button className="ghost" disabled={disabled || item.id === listing.folder.id} onClick={() => navigate(item.id)}>
        {index === 0 ? 'Documents' : item.name}
      </button>
    </span>)}
  </nav>
}

function FolderView({ folderId, navigate }: { folderId: string; navigate: (id: string) => void }) {
  const writable = canManageAdministration(useAppUser())
  const listing = useAsync((signal) => browseSharePoint(folderId, signal), [folderId])
  const [filter, setFilter] = useState('')
  const [action, setAction] = useState<Action | null>(null)
  const [busy, setBusy] = useState(false)
  const busyRef = useRef(false)
  const [uploadStatus, setUploadStatus] = useState('')
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')
  const [dragging, setDragging] = useState(false)
  const input = useRef<HTMLInputElement>(null)
  const locked = busy || listing.loading || !!listing.error || !listing.data
  const items = (listing.data?.items ?? []).filter(item => item.name.toLocaleLowerCase().includes(filter.toLocaleLowerCase()))

  async function upload(files: File[]) {
    if (busyRef.current || !writable || locked || files.length === 0) {
      return
    }
    busyRef.current = true
    setBusy(true)
    setError('')
    setNotice('')
    let succeeded = 0
    const failures: string[] = []
    try {
      for (const [index, file] of files.entries()) {
        setUploadStatus(`Uploading ${index + 1} of ${files.length}: ${file.name}`)
        if (file.size > 100 * 1024 * 1024) {
          failures.push(`${file.name}: exceeds the 100 MB limit.`)
          continue
        }
        try {
          await uploadSharePointFile(listing.data!.folder.id, file)
          succeeded++
        } catch (cause) {
          failures.push(`${file.name}: ${message(cause)}`)
        }
      }
      setNotice(`${succeeded} of ${files.length} files uploaded.`)
      setError(failures.join('\n'))
    } finally {
      setUploadStatus('')
      setBusy(false)
      busyRef.current = false
      listing.reload()
    }
  }

  function drop(event: DragEvent) {
    event.preventDefault()
    setDragging(false)
    if (!writable || locked) {
      return
    }
    const entries = Array.from(event.dataTransfer.items)
    if (entries.some(item => item.webkitGetAsEntry?.()?.isDirectory)) {
      setError('Drop files only. Create folders using New folder.')
      return
    }
    void upload(Array.from(event.dataTransfer.files))
  }

  return <div className="browse-page">
    <div className="page-head">
      <div><h1>Browse</h1><p>Files and folders in your SharePoint document library.</p></div>
      <button disabled={busy || listing.loading} onClick={listing.reload}><RefreshCw size={15} />Refresh</button>
    </div>
    {error && <ErrorBanner message={error} />}
    {notice && <div className="browse-notice" role="status">{notice}</div>}
    {listing.error && <ErrorBanner message={listing.error} onRetry={listing.reload} />}
    {listing.error && <button onClick={() => navigate('root')}>Back to Documents</button>}
    <section className={`panel browse-panel${dragging ? ' browse-dragging' : ''}`}
      onDragOver={(event) => {
        event.preventDefault()
        if (writable && !locked && event.dataTransfer.types.includes('Files')) {
          setDragging(true)
        }
      }}
      onDragLeave={(event) => {
        if (!event.currentTarget.contains(event.relatedTarget as Node | null)) {
          setDragging(false)
        }
      }} onDrop={drop}>
      <div className="browse-toolbar">
        {listing.data && <Breadcrumbs listing={listing.data} navigate={navigate} disabled={busy} />}
        <div className="browse-tools">
          <input type="search" aria-label="Filter this folder" placeholder="Filter this folder…" value={filter} onChange={event => setFilter(event.target.value)} />
          {writable && <>
            <button disabled={locked} onClick={() => setAction({ operation: 'new' })}><FolderPlus size={16} />New folder</button>
            <button disabled={locked} onClick={() => input.current?.click()}><Upload size={16} />Upload files</button>
            <input ref={input} type="file" multiple hidden onChange={event => {
              void upload(Array.from(event.target.files ?? []))
              event.target.value = ''
            }} />
          </>}
        </div>
      </div>
      <LoadingBar active={listing.loading || busy} />
      {uploadStatus && <p role="status" className="browse-help">{uploadStatus}</p>}
      {writable && <p className="browse-help">Drop files here or use Upload files · Up to 100 MB per file · Existing files are kept</p>}
      {!writable && <p className="browse-help">Read-only access</p>}
      {!listing.loading && !listing.error && listing.data && <>
        <div className="browse-table-wrap"><table className="browse-table">
          <thead><tr><th>Name</th><th>Modified</th><th>Size</th><th>Actions</th></tr></thead>
          <tbody>{items.map(item => <tr key={item.id}>
            <td><div className="browse-name">{item.isFolder ? <Folder size={19} /> : <File size={19} />}
              {item.isFolder ? <button className="ghost" disabled={busy} onClick={() => navigate(item.id)}>{item.name}</button> : <span>{item.name}</span>}
            </div></td>
            <td>{formatDateTime(item.lastModifiedUtc)}</td><td>{item.isFolder ? 'Folder' : formatBytes(item.size)}</td>
            <td><div className="browse-actions">
              {item.webUrl && <a href={item.webUrl} target="_blank" rel="noreferrer" aria-label={`Open ${item.name} in SharePoint`} title="Open in SharePoint"><ExternalLink size={15} /></a>}
              {writable && <>
                <button className="ghost icon-only" disabled={locked || !item.eTag} aria-label={`Rename ${item.name}`} title="Rename" onClick={() => setAction({ operation: 'rename', item })}><Pencil size={15} /></button>
                <button className="ghost icon-only" disabled={locked || !item.eTag} aria-label={`Copy ${item.name}`} title="Copy" onClick={() => setAction({ operation: 'copy', item })}><Copy size={15} /></button>
                <button className="ghost icon-only" disabled={locked || !item.eTag} aria-label={`Move ${item.name}`} title="Move" onClick={() => setAction({ operation: 'move', item })}><Move size={15} /></button>
                <button className="ghost icon-only" disabled={locked || !item.eTag} aria-label={`Delete ${item.name}`} title="Delete" onClick={() => setAction({ operation: 'delete', item })}><Trash2 size={15} /></button>
              </>}
            </div></td>
          </tr>)}</tbody>
        </table></div>
        {items.length === 0 && <Empty title={filter ? 'No matching files or folders' : 'This folder is empty'} detail={filter ? 'Try a different filter.' : 'Create a folder or upload files to get started.'} />}
        <p className="browse-help">{items.length} of {listing.data.items.length} items</p>
      </>}
    </section>
    {action && listing.data && <ActionDialog action={action} currentFolder={listing.data.folder}
      close={() => setAction(null)} completed={(text) => {
        setAction(null)
        setError('')
        setNotice(text)
        listing.reload()
      }} />}
  </div>
}

function ActionDialog({ action, currentFolder, close, completed }: { action: Action; currentFolder: BrowseItem; close: () => void; completed: (notice: string) => void }) {
  const [name, setName] = useState(action.item?.name ?? '')
  const [destinationId, setDestinationId] = useState(currentFolder.id)
  const [destination, setDestination] = useState<BrowseItem | null>(null)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState('')
  const transfer = action.operation === 'copy' || action.operation === 'move'
  const ActionIcon = actionIcons[action.operation]
  const nameValid = name.length > 0 && name.length <= 255 && name === name.trim() && !/["*:<>?/\\|\u0000-\u001f]/.test(name) && !name.endsWith('.')

  async function submit() {
    if (busy) {
      return
    }
    setBusy(true)
    setError('')
    try {
      if (action.operation === 'new') {
        await createSharePointFolder(currentFolder.id, name)
      } else {
        await changeSharePointItem(action.item!, action.operation, name, destination?.id)
      }
      completed(action.operation === 'copy'
        ? 'Copy accepted by SharePoint. It continues in the background; refresh the destination folder to check the result.'
        : `${titles[action.operation]} completed.`)
    } catch (cause) {
      setError(message(cause))
      setBusy(false)
    }
  }

  return <Modal open title={titles[action.operation]} icon={<ActionIcon size={17} />} onClose={() => {
    if (!busy) {
      close()
    }
  }} footer={<>
    <button disabled={busy} onClick={close}><X size={15} aria-hidden="true" />Cancel</button>
    <button className={action.operation === 'delete' ? 'danger' : 'primary'} disabled={busy || (action.operation !== 'delete' && !nameValid) || (transfer && !destination)} onClick={() => void submit()}>
      <ActionIcon size={15} aria-hidden="true" />
      {busy ? 'Working…' : titles[action.operation]}
    </button>
  </>}>
    {error && <ErrorBanner message={error} />}
    {action.operation === 'delete'
      ? <p>Delete <strong>{action.item?.name}</strong>{action.item?.isFolder ? ' and everything inside it' : ''}? SharePoint moves deleted items to its recycle bin.</p>
      : <label className="browse-field">Name<input type="text" autoFocus value={name} maxLength={255} disabled={busy} onChange={event => setName(event.target.value)} /></label>}
    {transfer && <div className="browse-destination">
      <h3>Destination folder</h3>
      <FolderPicker key={destinationId} folderId={destinationId} sourceId={action.item!.id} disabled={busy}
        select={setDestination} navigate={(id) => {
          setDestination(null)
          setDestinationId(id)
        }} />
    </div>}
  </Modal>
}

function FolderPicker({ folderId, sourceId, disabled, select, navigate }: {
  folderId: string; sourceId: string; disabled: boolean; select: (item: BrowseItem | null) => void; navigate: (id: string) => void
}) {
  const listing = useAsync((signal) => browseSharePoint(folderId, signal), [folderId])
  const data = listing.data
  const invalid = data?.breadcrumbs.some(item => item.id === sourceId)
  return <div>
    <LoadingBar active={listing.loading} />
    {listing.error && <ErrorBanner message={listing.error} onRetry={listing.reload} />}
    {data && !listing.loading && !listing.error && <>
      <Breadcrumbs listing={data} navigate={navigate} disabled={disabled} />
      <div className="browse-folder-picker">
        {data.items.filter(item => item.isFolder && item.id !== sourceId).map(item => <button key={item.id} disabled={disabled} onClick={() => navigate(item.id)}><Folder size={16} />{item.name}</button>)}
        {!data.items.some(item => item.isFolder && item.id !== sourceId) && <p>No subfolders</p>}
      </div>
      <label className="browse-select-destination"><input type="radio" name="destination" disabled={disabled || invalid} onChange={() => select(data.folder)} />Use {data.folder.name} as destination</label>
      {invalid && <p>A folder cannot be copied or moved into itself.</p>}
    </>}
  </div>
}
