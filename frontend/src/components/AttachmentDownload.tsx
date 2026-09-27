import { useState, type ReactNode } from 'react'
import { downloadAttachmentFile } from '../api/client'

export function AttachmentDownload({ id, name, children }: { id: string; name: string; children: ReactNode }) {
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const download = async () => {
    setBusy(true)
    setError(null)
    try {
      const blob = await downloadAttachmentFile(id)
      const url = URL.createObjectURL(blob)
      const link = document.createElement('a')
      link.href = url
      link.download = name
      document.body.appendChild(link)
      link.click()
      link.remove()
      window.setTimeout(() => URL.revokeObjectURL(url), 1000)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Download failed.')
    } finally { setBusy(false) }
  }
  return <>
    <button disabled={busy} onClick={() => void download()} title={`Download ${name}`}>{children}</button>
    {error && <span role="alert">{error}</span>}
  </>
}
