import { useEffect, useRef, useState } from 'react'
import { Download, X } from 'lucide-react'
import { ErrorBanner, MaximizeButton, useViewerMaximized } from './ui'

export function ImageViewer({ name, sourceKey, load, onClose }: {
  name: string
  sourceKey: string
  load: (signal: AbortSignal) => Promise<Blob>
  onClose: () => void
}) {
  const dialogRef = useRef<HTMLDialogElement>(null)
  const closeRef = useRef<HTMLButtonElement>(null)
  const [maximized, toggleMaximized] = useViewerMaximized()
  const [url, setUrl] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    const dialog = dialogRef.current
    dialog?.showModal()
    closeRef.current?.focus()
    return () => dialog?.close()
  }, [])

  useEffect(() => {
    const controller = new AbortController()
    let objectUrl: string | undefined
    setUrl(null)
    setError(null)
    load(controller.signal).then(blob => {
      if (!controller.signal.aborted) {
        objectUrl = URL.createObjectURL(blob)
        setUrl(objectUrl)
      }
    }).catch(cause => {
      if (!controller.signal.aborted) {
        setError(cause instanceof Error ? cause.message : String(cause))
      }
    })
    return () => {
      controller.abort()
      if (objectUrl) {
        URL.revokeObjectURL(objectUrl)
      }
    }
    // sourceKey identifies the image; capture the loader for this request.
  }, [sourceKey])

  return <dialog ref={dialogRef} className={maximized ? 'office-dialog is-maximized' : 'office-dialog'} aria-label={`Preview ${name}`} onCancel={event => {
    event.preventDefault()
    onClose()
  }}>
    <div className="office-toolbar">
      <strong title={name}>{name}</strong>
      <div className="row">
        {url ? <a className="button-link" href={url} download={name}><Download size={14} />Download</a> : null}
        <MaximizeButton maximized={maximized} onToggle={toggleMaximized} />
        <button className="ghost" ref={closeRef} onClick={onClose}><X size={15} />Close</button>
      </div>
    </div>
    <div className="office-content image-preview-content">
      {error ? <ErrorBanner message={error} /> : !url ? <div className="office-message" role="status">Downloading image…</div> : (
        <img src={url} alt={name} onError={() => setError('This image could not be displayed. Download the original file to open it.')} />
      )}
    </div>
  </dialog>
}
