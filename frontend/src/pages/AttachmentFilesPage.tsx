import { useId, useState } from 'react'
import { Link } from 'react-router-dom'
import { Download, Eye, FileText, HardDrive, MessageSquare, Paperclip, RefreshCw, RotateCw, SearchX, Trash2, X } from 'lucide-react'
import {
  downloadAttachmentFile,
  getAttachmentFileMarkdown,
  deleteOrphanAttachmentFile,
  listAttachmentFiles,
  reindexAttachmentFile,
  getCurrentUser,
  getAttachmentOptions,
} from '../api/client'
import type { UploadIndexStatus } from '../api/types'
import { Empty, ErrorBanner, LoadingBar, Pagination } from '../components/ui'
import { FileTypeIcon } from '../components/FileTypeIcon'
import { AttachmentDownload } from '../components/AttachmentDownload'
import { OfficePreview } from '../components/OfficePreview'
import { ImagePreview } from '../components/ImagePreview'
import { MarkdownPreview } from '../components/MarkdownPreview'
import { AttachmentStorageUsage } from '../components/AttachmentStorageUsage'
import { SystemAttachmentStorage } from '../components/SystemAttachmentStorage'
import { canReadAdministration } from '../components/AppUserContext'
import { isPreviewableOfficeFile } from '../lib/officeFiles'
import { formatBytes, formatDateTime, formatRelative } from '../lib/format'
import { useAsync, useDebounced } from '../lib/useAsync'

const STATUS_LABELS: Record<UploadIndexStatus, string> = {
  NotStarted: 'Not started',
  Indexing: 'Indexing',
  Indexed: 'Indexed',
  Failed: 'Failed',
}

const STATUS_CLASSES: Record<UploadIndexStatus, string> = {
  NotStarted: '',
  Indexing: 'warning',
  Indexed: 'good',
  Failed: 'critical',
}

export default function AttachmentFilesPage() {
  const [tab, setTab] = useState<'files' | 'usage'>('files')
  const tabId = useId()
  const canReadSystemStorage = canReadAdministration(useAppUser())
  const readOnly = !canManageOwnContent(useAppUser())
  const [search, setSearch] = useState('')
  const [skip, setSkip] = useState(0)
  const [working, setWorking] = useState<string | null>(null)
  const [actionError, setActionError] = useState<string | null>(null)
  const [confirmDelete, setConfirmDelete] = useState<string | null>(null)
  const [preview, setPreview] = useState<{ id: string; name: string } | null>(null)
  const [imagePreview, setImagePreview] = useState<{ id: string; name: string } | null>(null)
  const [markdownFile, setMarkdownFile] = useState<{ id: string; name: string } | null>(null)
  const debouncedSearch = useDebounced(search)
  const storage = useAsync(signal => getCurrentUser(signal), [])
  const attachmentOptions = useAsync(signal => getAttachmentOptions(signal), [])
  const page = useAsync(
    (signal) => listAttachmentFiles({ search: debouncedSearch, skip, top: 25 }, signal),
    [debouncedSearch, skip],
  )

  const reindex = async (id: string) => {
    setWorking(id)
    setActionError(null)
    try {
      const result = await reindexAttachmentFile(id)
      if (result.status === 'Failed') setActionError(result.errorMessage ?? 'Indexing failed.')
      page.reload()
    } catch (cause) {
      setActionError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setWorking(null)
    }
  }

  const removeOrphan = async (id: string) => {
    setWorking(id)
    setActionError(null)
    try {
      await deleteOrphanAttachmentFile(id)
      storage.reload()
      setConfirmDelete(null)
      page.reload()
    } catch (cause) {
      setActionError(cause instanceof Error ? cause.message : String(cause))
    } finally {
      setWorking(null)
    }
  }

  return (
    <div className="stack">
      <div className="page-head attachment-page-header">
        <div>
          <h1><Paperclip size={20} />Attachment files</h1>
          <p>Manage chat attachments, indexing status, and storage usage.</p>
        </div>
        <section className="attachment-header-storage" aria-label="My attachment storage">
          <strong>My attachment storage</strong>
          {storage.data && <AttachmentStorageUsage used={storage.data.attachmentStorageUsedBytes} limit={storage.data.attachmentStorageLimitBytes} />}
          <LoadingBar active={storage.loading} />
          {storage.error && <ErrorBanner message={storage.error} onRetry={storage.reload} />}
        </section>
        <button onClick={() => { page.reload(); storage.reload() }}><RefreshCw size={14} />Refresh</button>
      </div>

      {canReadSystemStorage && <div className="attachment-tabs" role="tablist" aria-label="Attachment files views" onKeyDown={event => {
        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return
        event.preventDefault()
        const next = event.key === 'Home' ? 'files' : event.key === 'End' ? 'usage' : tab === 'files' ? 'usage' : 'files'
        setTab(next)
        event.currentTarget.querySelector<HTMLButtonElement>(`[data-tab="${next}"]`)?.focus()
      }}>
        <button role="tab" data-tab="files" id={`${tabId}-files-tab`} aria-controls={`${tabId}-files-panel`} aria-selected={tab === 'files'} tabIndex={tab === 'files' ? 0 : -1} onClick={() => setTab('files')}>
          <Paperclip size={15} aria-hidden="true" />Files
        </button>
        <button role="tab" data-tab="usage" id={`${tabId}-usage-tab`} aria-controls={`${tabId}-usage-panel`} aria-selected={tab === 'usage'} tabIndex={tab === 'usage' ? 0 : -1} onClick={() => setTab('usage')}>
          <HardDrive size={15} aria-hidden="true" />Storage usage
        </button>
      </div>}

      {canReadSystemStorage && <div role="tabpanel" id={`${tabId}-usage-panel`} aria-labelledby={`${tabId}-usage-tab`} hidden={tab !== 'usage'} tabIndex={0}>
      <div className="stack">
      {tab === 'usage' && canReadSystemStorage && <SystemAttachmentStorage refreshKey={page.data} />}
      </div>
      </div>}
      <div role={canReadSystemStorage ? 'tabpanel' : undefined} id={`${tabId}-files-panel`} aria-labelledby={canReadSystemStorage ? `${tabId}-files-tab` : undefined} hidden={canReadSystemStorage && tab !== 'files'} tabIndex={0}>
      <div className="stack">
      <div className="card"><div className="card-body">
        <input
          type="search"
          aria-label="Filter attachment files"
          placeholder="Filter by file name"
          value={search}
          onChange={(event) => { setSearch(event.target.value); setSkip(0) }}
        />
      </div></div>

      <LoadingBar active={page.loading || working !== null} />
      {page.error ? <ErrorBanner message={page.error} onRetry={page.reload} /> : null}
      {actionError ? <ErrorBanner message={actionError} /> : null}

      <div className="card">
        {page.data && page.data.items.length > 0 ? (
          <>
            <div className="table-scroll">
              <table>
                <thead><tr><th>File</th><th>Status</th><th>Conversation</th><th>Size</th><th>Chunks</th><th title="Embedding tokens used to index this attachment">Tokens</th><th>Uploaded</th><th>Indexed</th><th>Actions</th></tr></thead>
                <tbody>
                  {page.data.items.map((file) => (
                    <tr key={file.id}>
                      <td>
                        <span className="file-name">
                          <FileTypeIcon name={file.fileName} mimeType={file.contentType} size={15} />
                          <span>{file.fileName}</span>
                        </span>
                        {file.errorMessage ? <div className="upload-error" title={file.errorMessage}>{file.errorMessage}</div> : null}
                      </td>
                      <td><span className={`badge ${STATUS_CLASSES[file.status]}`}>{STATUS_LABELS[file.status]}</span></td>
                      <td>
                        {file.conversationId ? (
                          <Link
                            className="conversation-link"
                            to={`/chat?conversation=${encodeURIComponent(file.conversationId)}${file.messageId ? `&message=${encodeURIComponent(file.messageId)}` : ''}`}
                          >
                            <MessageSquare size={13} />
                            {file.conversationTitle ?? 'Open conversation'}
                          </Link>
                        ) : <span className="badge warning">Orphan</span>}
                      </td>
                      <td>{formatBytes(file.sizeBytes)}</td>
                      <td>{file.chunkCount.toLocaleString()}</td>
                      <td>{file.embeddingTokenCount === null ? '—' : file.embeddingTokenCount.toLocaleString()}</td>
                      <td title={formatDateTime(file.createdAtUtc)}>{formatRelative(file.createdAtUtc)}</td>
                      <td title={formatDateTime(file.indexedAtUtc)}>{formatRelative(file.indexedAtUtc)}</td>
                      <td>
                        <div className="row" style={{ gap: 6, flexWrap: 'wrap' }}>
                          <AttachmentDownload id={file.id} name={file.fileName}><Download size={13} />Download</AttachmentDownload>
                          {attachmentOptions.data?.imageFileExtensions.includes(file.fileName.slice(file.fileName.lastIndexOf('.')).toLowerCase()) ? (
                            <button onClick={() => setImagePreview({ id: file.id, name: file.fileName })}><Eye size={13} />Preview</button>
                          ) : null}
                          {isPreviewableOfficeFile(file.fileName) ? (
                            <button onClick={() => setPreview({ id: file.id, name: file.fileName })}><Eye size={13} />Preview</button>
                          ) : null}
                          {attachmentOptions.data && ![...attachmentOptions.data.textFileExtensions, ...attachmentOptions.data.imageFileExtensions].includes(file.fileName.slice(file.fileName.lastIndexOf('.')).toLowerCase()) ? (
                            <button onClick={() => setMarkdownFile({ id: file.id, name: file.fileName })}>
                              <FileText size={13} />View Markdown
                            </button>
                          ) : null}
                          <button disabled={readOnly || working === file.id} onClick={() => void reindex(file.id)}>
                            <RotateCw size={13} />{working === file.id ? 'Indexing…' : 'Reindex'}
                          </button>
                          {file.isOrphan ? (
                            confirmDelete === file.id ? (
                              <>
                                <button className="ghost icon-only" title="Cancel" onClick={() => setConfirmDelete(null)}><X size={13} /></button>
                                <button className="danger" disabled={working === file.id} onClick={() => void removeOrphan(file.id)}>
                                  <Trash2 size={13} />Confirm
                                </button>
                              </>
                            ) : (
                              <button className="danger" disabled={readOnly} onClick={() => setConfirmDelete(file.id)}>
                                <Trash2 size={13} />Delete orphan
                              </button>
                            )
                          ) : null}
                        </div>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <Pagination skip={skip} top={25} total={page.data.totalCount} onSkip={setSkip} />
          </>
        ) : page.loading ? <Empty title="Loading…" /> : (
          <Empty title="No attachment files" icon={<SearchX size={26} strokeWidth={1.5} />} detail="Files attached in chat will appear here." />
        )}
      </div>
      </div>
      </div>
      {preview ? (
        <OfficePreview
          key={preview.id}
          name={preview.name}
          sourceKey={preview.id}
          load={(signal) => downloadAttachmentFile(preview.id, signal)}
          onClose={() => setPreview(null)}
        />
      ) : null}
      {markdownFile ? (
        <MarkdownPreview
          key={markdownFile.id}
          name={markdownFile.name}
          sourceKey={markdownFile.id}
          load={(signal) => getAttachmentFileMarkdown(markdownFile.id, signal)}
          onClose={() => setMarkdownFile(null)}
        />
      ) : null}
      {imagePreview ? (
        <ImagePreview
          key={imagePreview.id}
          name={imagePreview.name}
          sourceKey={imagePreview.id}
          load={(signal) => downloadAttachmentFile(imagePreview.id, signal)}
          onClose={() => setImagePreview(null)}
        />
      ) : null}
    </div>
  )
}
import { useAppUser, canManageOwnContent } from '../components/AppUserContext'
