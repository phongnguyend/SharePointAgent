import { useEffect, useRef, useState, type ReactNode } from 'react'
import { Upload } from 'lucide-react'
import { uploadOrphanAttachmentFile } from '../api/client'
import { formatBytes } from '../lib/format'
import { ErrorBanner } from './ui'

export function OrphanAttachmentUpload({ extensions, maxFileBytes, onUploaded, children }: {
  extensions: string[]; maxFileBytes: number; onUploaded: () => void; children?: ReactNode
}) {
  const input = useRef<HTMLInputElement>(null)
  const request = useRef<AbortController | null>(null)
  const dragDepth = useRef(0)
  const [dragging, setDragging] = useState(false)
  const [progress, setProgress] = useState<string | null>(null)
  const [result, setResult] = useState('')
  const [errors, setErrors] = useState<string[]>([])
  useEffect(() => () => request.current?.abort(), [])

  const upload = async (files: File[]) => {
    if (request.current || files.length === 0) {
      return
    }
    const controller = new AbortController()
    request.current = controller
    setErrors([])
    setResult('')
    const failures: string[] = []
    let uploaded = 0
    try {
      for (const [index, file] of files.entries()) {
        setProgress(`Uploading ${index + 1} of ${files.length}: ${file.name}`)
        try {
          const extension = file.name.slice(file.name.lastIndexOf('.')).toLowerCase()
          if (!extensions.includes(extension)) {
            throw new Error('This file type is not allowed.')
          }
          if (file.size === 0 || file.size > maxFileBytes) {
            throw new Error(`Files must be non-empty and no larger than ${formatBytes(maxFileBytes)}.`)
          }
          await uploadOrphanAttachmentFile(file, controller.signal)
          uploaded++
        } catch (cause) {
          if (controller.signal.aborted) {
            return
          }
          failures.push(`${file.name}: ${cause instanceof Error ? cause.message : String(cause)}`)
        }
      }
      setErrors(failures)
      setResult(`${uploaded} of ${files.length} files uploaded without indexing.`)
      if (uploaded > 0) {
        onUploaded()
      }
    } finally {
      request.current = null
      if (!controller.signal.aborted) {
        setProgress(null)
      }
    }
  }

  return <section className={`orphan-upload${dragging ? ' is-dragging' : ''}`} aria-label="Upload attachment files" aria-busy={!!progress}
    onDragEnter={event => {
      if (event.dataTransfer.types.includes('Files')) {
        event.preventDefault()
        dragDepth.current++
        setDragging(true)
      }
    }}
    onDragOver={event => {
      if (event.dataTransfer.types.includes('Files')) {
        event.preventDefault()
        event.dataTransfer.dropEffect = progress ? 'none' : 'copy'
      }
    }}
    onDragLeave={event => {
      event.preventDefault()
      dragDepth.current = Math.max(0, dragDepth.current - 1)
      if (dragDepth.current === 0) {
        setDragging(false)
      }
    }}
    onDrop={event => {
      event.preventDefault()
      dragDepth.current = 0
      setDragging(false)
      void upload(Array.from(event.dataTransfer.files))
    }}>
    <div className="row orphan-upload-toolbar">
      <button disabled={!!progress || extensions.length === 0} onClick={() => input.current?.click()}><Upload size={15} />Upload files</button>
      <span className="orphan-upload-hint">{dragging ? 'Drop files to upload' : `Drop files · ${formatBytes(maxFileBytes)} max · Not indexed`}</span>
      {children}
    </div>
    <input ref={input} type="file" multiple hidden accept={extensions.join(',')} aria-label="Choose attachment files"
      disabled={!!progress || extensions.length === 0} onChange={event => {
        const files = Array.from(event.target.files ?? [])
        event.target.value = ''
        void upload(files)
      }} />
    {(progress || result) && <p role="status">{progress || result}</p>}
    {errors.map((error, index) => <ErrorBanner key={index} message={error} />)}
  </section>
}
