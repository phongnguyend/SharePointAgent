import { useCallback, useEffect, useRef, useState } from 'react'
import { Image as ImageIcon, Play, RotateCcw, Square, Upload } from 'lucide-react'
import { evaluateImage, type ImageEvaluation, type ImageOperation } from '../api/imagePlaygroundClient'
import { CopyButton, ErrorBanner } from '../components/ui'
import { PlaygroundRequestTabs, type PlaygroundRequestTab } from '../components/PlaygroundRequestTabs'

const formats = {
  describe: ['.png', '.jpg', '.jpeg', '.webp', '.gif'],
  extract: ['.png', '.jpg', '.jpeg', '.bmp', '.tif', '.tiff'],
}
const clipboardExtensions: Record<string, string> = {
  'image/png': '.png', 'image/jpeg': '.jpg', 'image/webp': '.webp', 'image/gif': '.gif',
  'image/bmp': '.bmp', 'image/tiff': '.tiff',
}

export default function ImagePlaygroundPage({ operation, active = true }: { operation: ImageOperation; active?: boolean }) {
  return <PlaygroundRequestTabs label={operation === 'describe' ? 'Image Description' : 'Image Text Extraction'} active={active}>
    {tab => <ImageRequest operation={operation} tab={tab} />}
  </PlaygroundRequestTabs>
}

function ImageRequest({ operation, tab }: { operation: ImageOperation; tab: PlaygroundRequestTab }) {
  const { active, id, onSummary } = tab
  const [file, setFile] = useState<File | null>(null)
  const [url, setUrl] = useState<string | null>(null)
  const [previewFailed, setPreviewFailed] = useState(false)
  const [dragging, setDragging] = useState(false)
  const [running, setRunning] = useState(false)
  const [error, setError] = useState('')
  const [result, setResult] = useState<ImageEvaluation | null>(null)
  const input = useRef<HTMLInputElement>(null)
  const controller = useRef<AbortController | null>(null)
  const depth = useRef(0)
  const action = operation === 'describe' ? 'Describe' : 'Extract'
  const title = operation === 'describe' ? 'Image Description' : 'Image Text Extraction'
  useEffect(() => () => controller.current?.abort(), [])
  useEffect(() => {
    onSummary(id, file?.name ?? '', running)
  }, [id, file?.name, running, onSummary])
  useEffect(() => {
    if (!active) {
      depth.current = 0
      setDragging(false)
    }
  }, [active])
  useEffect(() => {
    setPreviewFailed(false)
    if (!file) {
      setUrl(null)
      return
    }
    const objectUrl = URL.createObjectURL(file)
    setUrl(objectUrl)
    return () => URL.revokeObjectURL(objectUrl)
  }, [file])
  const select = useCallback((files: FileList | File[] | null) => {
    if (running || !files?.length) {
      return
    }
    if (files.length !== 1 || !formats[operation].some(ext => files[0].name.toLowerCase().endsWith(ext)) || files[0].size === 0 || files[0].size > 10 * 1024 * 1024) {
      setError(`Select one image up to 10 MB: ${formats[operation].join(', ')}.`)
      return
    }
    setFile(files[0])
    setResult(null)
    setError('')
  }, [operation, running])
  useEffect(() => {
    if (!active) {
      return
    }
    const paste = (event: ClipboardEvent) => {
      if (running || event.defaultPrevented || document.querySelector('dialog[open]') ||
        (event.target instanceof Element && event.target.closest('input, textarea, [contenteditable="true"]'))) {
        return
      }
      const images = Array.from(event.clipboardData?.files ?? []).filter(image => image.type.startsWith('image/'))
      if (images.length === 0) {
        return
      }
      event.preventDefault()
      select(images.map(image => {
        const extension = clipboardExtensions[image.type]
        if (extension && !/\.[a-z0-9]+$/i.test(image.name)) {
          return new File([image], `${image.name || 'pasted-image'}${extension}`, { type: image.type, lastModified: image.lastModified })
        }
        return image
      }))
    }
    document.addEventListener('paste', paste)
    return () => document.removeEventListener('paste', paste)
  }, [active, running, select])
  const run = async () => {
    if (!file || controller.current) {
      return
    }
    const pending = new AbortController()
    controller.current = pending
    setRunning(true)
    setError('')
    setResult(null)
    try {
      const response = await evaluateImage(operation, file, pending.signal)
      if (!pending.signal.aborted) {
        setResult(response)
      }
    } catch (cause) {
      if (!pending.signal.aborted) {
        setError(cause instanceof Error ? cause.message : 'Image processing failed.')
      }
    } finally {
      controller.current = null
      setRunning(false)
    }
  }
  return <section className="card decision-playground">
    <div className="card-head decision-toolbar">
      <h2><ImageIcon size={18} />{title}</h2>
    </div>
    <div className="card-body stack">
      <p className="decision-description">{operation === 'describe'
        ? 'Describe an image using the configured vision model. Token usage is recorded under your user in Image Description usage.'
        : 'Extract visible text using Document Intelligence OCR.'} Runs may incur provider charges.</p>
      <div className="playground-request-boundary">
      {tab.tabBar}
      <div className="stack playground-request-content" role="tabpanel" id={tab.panelId} aria-labelledby={tab.tabId}>
      {error ? <ErrorBanner message={error} /> : null}
      <div className="decision-playground-grid">
        <div className="decision-panel">
          <div className="decision-panel-heading playground-request-heading">
            <h3>Image</h3>
            <div className="row">
              <button className="primary" disabled={running || !file} onClick={() => void run()}><Play size={15} />{running ? 'Processing...' : action}</button>
              {running ? <button onClick={() => {
                controller.current?.abort()
                setError('Request cancelled. The provider may already have processed it.')
              }}><Square size={15} />Cancel</button> : null}
              <button disabled={running} onClick={() => {
                setFile(null)
                setResult(null)
                setError('')
              }}><RotateCcw size={15} />Reset</button>
              <button disabled={running} onClick={() => input.current?.click()}><Upload size={14} />Select file</button>
            </div>
          </div>
          <input ref={input} type="file" accept={formats[operation].join(',')} hidden disabled={running} aria-label={`Select image for ${title}`} onChange={event => {
            select(event.target.files)
            event.target.value = ''
          }} />
          <div className={`image-playground-drop${dragging ? ' is-dragging' : ''}`} aria-busy={running} tabIndex={0} aria-label="Image upload area. Paste an image with Ctrl+V or Command+V."
            onDragEnter={event => {
              if (event.dataTransfer.types.includes('Files')) {
                event.preventDefault()
                depth.current += 1
                setDragging(!running)
              }
            }} onDragOver={event => {
              event.preventDefault()
              event.dataTransfer.dropEffect = running ? 'none' : 'copy'
            }} onDragLeave={() => {
              depth.current = Math.max(0, depth.current - 1)
              if (depth.current === 0) {
                setDragging(false)
              }
            }} onDrop={event => {
              event.preventDefault()
              event.stopPropagation()
              depth.current = 0
              setDragging(false)
              select(event.dataTransfer.files)
            }}>
            {url && !previewFailed ? <img src={url} alt={file?.name ?? 'Selected image'} onError={() => setPreviewFailed(true)} /> : <ImageIcon size={36} aria-hidden="true" />}
            <p>{dragging ? 'Drop image here' : file ? file.name : 'Drop an image, paste from clipboard, or select a file'}</p>
            <small className="hint">Paste an image with Ctrl+V or Command+V.</small>
            {previewFailed ? <p className="hint">Browser preview is unavailable for this image. You can still submit it.</p> : null}
            <small className="hint">{formats[operation].join(', ')} · Maximum 10 MB</small>
          </div>
        </div>
        <div className="decision-panel" aria-busy={running}>
          <div className="decision-panel-heading"><h3>{operation === 'describe' ? 'Description' : 'Extracted text'}</h3>{result ? <CopyButton value={result.text} /> : null}</div>
          {result ? <pre className="image-playground-result" tabIndex={0}>{result.text || 'No text was detected.'}</pre>
            : <div className="decision-response-empty" role="status"><p>{running ? 'Processing image...' : `Select an image and click ${action}.`}</p></div>}
        </div>
      </div>
      {result ? <section className="decision-summary" aria-label="Image processing summary"><h3>Summary</h3><dl>
        <div><dt>Model</dt><dd>{result.modelId ?? 'Unavailable'}</dd></div>
        {result.usage ? <><div><dt>Input tokens</dt><dd>{result.usage.inputTokens.toLocaleString()}</dd></div><div><dt>Output tokens</dt><dd>{result.usage.outputTokens.toLocaleString()}</dd></div><div><dt>Total tokens</dt><dd>{result.usage.totalTokens.toLocaleString()}</dd></div></> : null}
        <div><dt>Duration</dt><dd>{result.elapsedMs.toLocaleString()} ms</dd></div>
        <div className="decision-trace"><dt>Trace ID</dt><dd>{result.traceId ?? 'Unavailable'}{result.traceId ? <CopyButton value={result.traceId} /> : null}</dd></div>
      </dl></section> : null}
      </div>
      </div>
    </div>
  </section>
}
