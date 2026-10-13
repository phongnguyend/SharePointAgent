import { useEffect, useState } from 'react'
import { Download, X } from 'lucide-react'
import { ErrorBanner, Modal, MaximizeButton, useViewerMaximized } from './ui'

export function ImageViewer({ name, sourceKey, load, onClose }: {
  name: string
  sourceKey: string
  load: (signal: AbortSignal) => Promise<Blob>
  onClose: () => void
}) {
  const [maximized, toggleMaximized] = useViewerMaximized()
  const [url, setUrl] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)


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

  return <Modal open title={`Preview ${name}`} className={maximized ? 'office-dialog is-maximized' : 'office-dialog'} bodyClassName="modal-preview-body" onClose={onClose}
      headerActions={<div className="row">
        {url ? <a className="button-link" href={url} download={name}><Download size={14} />Download</a> : null}
        <MaximizeButton maximized={maximized} onToggle={toggleMaximized} />
        <button className="ghost" autoFocus onClick={onClose}><X size={15} />Close</button>
      </div>}>
      <div className="office-content image-preview-content">
      {error ? <ErrorBanner message={error} /> : !url ? <div className="office-message" role="status">Downloading image…</div> : (
        <img src={url} alt={name} onError={() => setError('This image could not be displayed. Download the original file to open it.')} />
      )}
    </div>
  </Modal>
}
