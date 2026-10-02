import { useEffect, useId, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import { Download, Eye, FileText, HardDrive, MessageSquare, Paperclip, RefreshCw, RotateCw, SearchX, Trash2, X } from 'lucide-react'
import {
  downloadAttachmentFile,
  getAttachmentFileMarkdown,
  convertAttachmentFileToMarkdown,
  deleteOrphanAttachmentFile,
  listAttachmentFiles,
  reindexAttachmentFile,
  getCurrentUser,
  getAttachmentOptions,
  describeAttachmentImage,
  extractAttachmentText,
} from '../api/client'
import type { UploadIndexStatus } from '../api/types'
import { CopyButton, Empty, ErrorBanner, LoadingBar, Modal, Pagination } from '../components/ui'
import { FileTypeIcon } from '../components/FileTypeIcon'
import { AttachmentDownload } from '../components/AttachmentDownload'
import { OfficeViewer } from '../components/OfficeViewer'
import { ImageViewer } from '../components/ImageViewer'
import { PdfViewer } from '../components/PdfViewer'
import { SignatureRequests } from '../components/SignatureRequests'
import { OrphanAttachmentUpload } from '../components/OrphanAttachmentUpload'
import { MarkdownViewer } from '../components/MarkdownViewer'
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
  const [pdfPreview, setPdfPreview] = useState<{ id: string; name: string } | null>(null)
  const [signatureFile, setSignatureFile] = useState<{ id: string; name: string } | null>(null)
  const [markdownFile, setMarkdownFile] = useState<{ id: string; name: string; mode: 'indexed' | 'convert' } | null>(null)
  const [imageDescriptionResult, setImageDescriptionResult] = useState<AttachmentTextResult | null>(null)
  const imageDescriptionRequest = useRef<AbortController | null>(null)
  useEffect(() => () => imageDescriptionRequest.current?.abort(), [])

  const describeImage = async (id: string, name: string) => {
    imageDescriptionRequest.current?.abort()
    const controller = new AbortController()
    imageDescriptionRequest.current = controller
    setImageDescriptionResult({ name })
    try {
      const result = await describeAttachmentImage(id, controller.signal)
      if (!controller.signal.aborted) {
        setImageDescriptionResult({ name, text: result.text || ('No image description was returned.') })
      }
    } catch (cause) {
      if (!controller.signal.aborted) {
        setImageDescriptionResult({ name, error: cause instanceof Error ? cause.message : String(cause) })
      }
    }
  }

  const closeImageDescription = () => {
    imageDescriptionRequest.current?.abort()
    setImageDescriptionResult(null)
  }

  const [extractTextResult, setExtractTextResult] = useState<AttachmentTextResult | null>(null)
  const extractTextRequest = useRef<AbortController | null>(null)
  useEffect(() => () => extractTextRequest.current?.abort(), [])

  const extractText = async (id: string, name: string) => {
    extractTextRequest.current?.abort()
    const controller = new AbortController()
    extractTextRequest.current = controller
    setExtractTextResult({ name })
    try {
      const result = await extractAttachmentText(id, controller.signal)
      if (!controller.signal.aborted) {
        setExtractTextResult({ name, text: result.text || (name.toLowerCase().endsWith('.pdf') ? 'No text was found in this PDF.' : 'No text was found in this image.') })
      }
    } catch (cause) {
      if (!controller.signal.aborted) {
        setExtractTextResult({ name, error: cause instanceof Error ? cause.message : String(cause) })
      }
    }
  }

  const closeExtractText = () => {
    extractTextRequest.current?.abort()
    setExtractTextResult(null)
  }
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

  const fileFilter = <input type="search" className="attachment-file-filter" aria-label="Filter attachment files"
    placeholder="Filter by file name" value={search} onChange={event => {
      setSearch(event.target.value)
      setSkip(0)
    }} />

  return (
    <div className="stack attachment-files-page">
      <div className="page-head attachment-page-header">
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

        <section className="attachment-header-storage" aria-label="My attachment storage" title="My attachment storage">
          {storage.data && <AttachmentStorageUsage used={storage.data.attachmentStorageUsedBytes} limit={storage.data.attachmentStorageLimitBytes} />}
          <LoadingBar active={storage.loading} />
          {storage.error && <ErrorBanner message={storage.error} onRetry={storage.reload} />}
        </section>
        <button onClick={() => {
          page.reload()
          storage.reload()
        }}><RefreshCw size={14} />Refresh</button>
      </div>

      {canReadSystemStorage && <div role="tabpanel" id={`${tabId}-usage-panel`} aria-labelledby={`${tabId}-usage-tab`} hidden={tab !== 'usage'} tabIndex={0}>
      <div className="stack">
      {tab === 'usage' && canReadSystemStorage && <SystemAttachmentStorage refreshKey={page.data} />}
      </div>
      </div>}
      <div role={canReadSystemStorage ? 'tabpanel' : undefined} id={`${tabId}-files-panel`} aria-labelledby={canReadSystemStorage ? `${tabId}-files-tab` : undefined} hidden={canReadSystemStorage && tab !== 'files'} tabIndex={0}>
      <div className="stack">
      {!readOnly && attachmentOptions.data ? <OrphanAttachmentUpload
        extensions={attachmentOptions.data.allowedFileExtensions} maxFileBytes={attachmentOptions.data.maxFileBytes}
        onUploaded={() => {
          setSearch('')
          setSkip(0)
          page.reload()
          storage.reload()
        }}>{fileFilter}</OrphanAttachmentUpload> : <div className="attachment-filter-toolbar">{fileFilter}</div>}
      {attachmentOptions.error && <ErrorBanner message={attachmentOptions.error} onRetry={attachmentOptions.reload} />}

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
                          {attachmentOptions.data?.imageDescriptionExtensions?.includes(file.fileName.slice(file.fileName.lastIndexOf('.')).toLowerCase()) && (
                            <button disabled={readOnly} onClick={() => void describeImage(file.id, file.fileName)}><Eye size={13} />Describe image</button>
                          )}
                          {(file.fileName.toLowerCase().endsWith('.pdf') || attachmentOptions.data?.imageTextExtensions?.includes(file.fileName.slice(file.fileName.lastIndexOf('.')).toLowerCase())) && (
                            <button disabled={readOnly} onClick={() => void extractText(file.id, file.fileName)}><FileText size={13} />Extract text</button>
                          )}
                          {attachmentOptions.data?.imageFileExtensions.includes(file.fileName.slice(file.fileName.lastIndexOf('.')).toLowerCase()) ? (
                            <button onClick={() => setImagePreview({ id: file.id, name: file.fileName })}><Eye size={13} />Preview</button>
                          ) : null}
                          {isPreviewableOfficeFile(file.fileName) ? (
                            <button onClick={() => setPreview({ id: file.id, name: file.fileName })}><Eye size={13} />Preview</button>
                          ) : null}
                          {file.fileName.toLowerCase().endsWith('.pdf') ? (
                            <>
                              <button onClick={() => setPdfPreview({ id: file.id, name: file.fileName })}><Eye size={13} />Preview</button>
                              <button onClick={() => setSignatureFile({ id: file.id, name: file.fileName })}><FileText size={13} />Signatures</button>
                            </>
                          ) : null}
                          <button disabled={file.status !== 'Indexed'} title="View the saved text used for indexing. Reindex to update it."
                            onClick={() => setMarkdownFile({ id: file.id, name: file.fileName, mode: 'indexed' })}>
                            <FileText size={13} />View indexed text
                          </button>
                          {attachmentOptions.data && ![...attachmentOptions.data.textFileExtensions, ...attachmentOptions.data.imageFileExtensions].includes(file.fileName.slice(file.fileName.lastIndexOf('.')).toLowerCase()) ? (
                            <button disabled={readOnly} title="Convert the original file now without changing its index."
                              onClick={() => setMarkdownFile({ id: file.id, name: file.fileName, mode: 'convert' })}>
                              <FileText size={13} />Convert to Markdown
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
          <Empty title="No attachment files" icon={<SearchX size={26} strokeWidth={1.5} />} detail="Upload files here or attach them in chat." />
        )}
      </div>
      </div>
      </div>
      <AttachmentTextResultModal result={imageDescriptionResult} title="Describe image"
        processingMessage="Processing image?" onClose={closeImageDescription} />
      <AttachmentTextResultModal result={extractTextResult} title="Extract text"
        className={extractTextResult?.name.toLowerCase().endsWith('.pdf') ? 'pdf-extracted-text-modal' : undefined}
        processingMessage={extractTextResult?.name.toLowerCase().endsWith('.pdf') ? 'Processing PDF?' : 'Processing image?'}
        onClose={closeExtractText} />
      {preview ? (
        <OfficeViewer
          key={preview.id}
          name={preview.name}
          sourceKey={preview.id}
          load={(signal) => downloadAttachmentFile(preview.id, signal)}
          onClose={() => setPreview(null)}
        />
      ) : null}
      {markdownFile ? (
        <MarkdownViewer
          key={`${markdownFile.id}:${markdownFile.mode}`}
          name={markdownFile.name}
          title={markdownFile.mode === 'indexed' ? 'View indexed text' : 'Convert to Markdown'}
          sourceKey={`${markdownFile.id}:${markdownFile.mode}`}
          load={(signal) => markdownFile.mode === 'indexed'
            ? getAttachmentFileMarkdown(markdownFile.id, signal)
            : convertAttachmentFileToMarkdown(markdownFile.id, signal)}
          onClose={() => setMarkdownFile(null)}
        />
      ) : null}
      {imagePreview ? (
        <ImageViewer
          key={imagePreview.id}
          name={imagePreview.name}
          sourceKey={imagePreview.id}
          load={(signal) => downloadAttachmentFile(imagePreview.id, signal)}
          onClose={() => setImagePreview(null)}
        />
      ) : null}
      {pdfPreview ? (
        <PdfViewer
          key={pdfPreview.id}
          name={pdfPreview.name}
          sourceKey={pdfPreview.id}
          load={(signal) => downloadAttachmentFile(pdfPreview.id, signal)}
          onSignatures={() => {
            setSignatureFile(pdfPreview)
            setPdfPreview(null)
          }}
          onClose={() => setPdfPreview(null)}
        />
      ) : null}
      {signatureFile && <SignatureRequests key={signatureFile.id} id={signatureFile.id} name={signatureFile.name}
        readOnly={readOnly} onClose={() => setSignatureFile(null)} />}
    </div>
  )
}
import { useAppUser, canManageOwnContent } from '../components/AppUserContext'


type AttachmentTextResult = { name: string; text?: string; error?: string }

function AttachmentTextResultModal({ result, title, processingMessage, className, onClose }: {
  result: AttachmentTextResult | null
  title: string
  processingMessage: string
  className?: string
  onClose: () => void
}) {
  const loading = !!result && result.text === undefined && !result.error
  return <Modal open={result !== null} title={title} className={className} onClose={onClose}
    footer={<>
      {result?.text && <CopyButton value={result.text} />}
      <button onClick={onClose}>Close</button>
    </>}>
    <p>{result?.name}</p>
    <LoadingBar active={loading} />
    {loading && <p role="status">{processingMessage}</p>}
    {result?.error && <ErrorBanner message={result.error} />}
    {result?.text && <pre style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', fontFamily: 'inherit' }}>{result.text}</pre>}
  </Modal>
}
