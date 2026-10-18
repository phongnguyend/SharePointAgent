import { PlaygroundRequestTabs, type PlaygroundRequestTab } from '../components/PlaygroundRequestTabs'
import { useEffect, useRef, useState } from 'react'
import { Download, FileText, Play, RotateCcw, Square, Upload } from 'lucide-react'
import ReactMarkdown from 'react-markdown'
import remarkGfm from 'remark-gfm'
import { convertPlaygroundDocument, indexPlaygroundDocument, parsePlaygroundDocument, type DocumentPlaygroundProvider, type PageIndexTree, type ParserWarning } from '../api/documentPlaygroundClient'
import { CopyButton, ErrorBanner } from '../components/ui'
import { JsonCode } from '../components/JsonCode'
import SourceCode from '../components/SourceCode'

type Evaluation = { text: string; tree?: PageIndexTree; warnings?: ParserWarning[]; elapsedMs: number; traceId: string | null; fileName: string }

export default function DocumentPlaygroundPage({ provider, active = true }: { provider: DocumentPlaygroundProvider; active?: boolean }) {
  return <PlaygroundRequestTabs label={provider === 'markitdown' ? 'MarkItDown' : provider === 'pageindex' ? 'PageIndex' : provider.toUpperCase()} active={active}>
    {tab => <DocumentRequest provider={provider} tab={tab} />}
  </PlaygroundRequestTabs>
}

function DocumentRequest({ provider, tab }: { provider: DocumentPlaygroundProvider; tab: PlaygroundRequestTab }) {
  const { active, id, onSummary } = tab
  const [file, setFile] = useState<File | null>(null)
  const [includeText, setIncludeText] = useState(true)
  const [includeSummaries, setIncludeSummaries] = useState(false)
  const [skipImages, setSkipImages] = useState(true)
  const [enrichImages, setEnrichImages] = useState(false)
  const [preview, setPreview] = useState(false)
  const [dragging, setDragging] = useState(false)
  const [running, setRunning] = useState(false)
  const [error, setError] = useState('')
  const [result, setResult] = useState<Evaluation | null>(null)
  const input = useRef<HTMLInputElement>(null)
  const controller = useRef<AbortController | null>(null)
  const depth = useRef(0)
  const isIndex = provider === 'pageindex'
  const isParser = provider !== 'markitdown' && provider !== 'pageindex'
  const name = isIndex ? 'PageIndex' : isParser ? `${provider.toUpperCase()} parser` : 'MarkItDown'
  const accept = isIndex ? '.pdf,.md,.markdown' : isParser ? `.${provider}` : undefined
  const action = isIndex ? 'Index' : 'Convert'
  useEffect(() => () => controller.current?.abort(), [])
  useEffect(() => {
    onSummary(id, file?.name ?? '', running)
  }, [file, id, onSummary, running])
  useEffect(() => {
    if (!active) {
      depth.current = 0
      setDragging(false)
    }
  }, [active])
  const select = (files: FileList | null) => {
    if (running || !files?.length) {
      return
    }
    if (files.length !== 1 || files[0].size === 0 || files[0].size > 25 * 1024 * 1024 ||
      (accept && !accept.split(',').some(extension => files[0].name.toLowerCase().endsWith(extension)))) {
      setError(`Select one non-empty ${isIndex ? 'PDF or Markdown file' : isParser ? provider.toUpperCase() + ' file' : 'document'} up to 25 MB.`)
      return
    }
    setFile(files[0])
    setResult(null)
    setError('')
  }
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
      if (isIndex) {
        const response = await indexPlaygroundDocument(file, includeText, includeSummaries, pending.signal)
        if (!pending.signal.aborted) {
          setResult({ ...response, text: JSON.stringify(response.result, null, 2), tree: response.result, fileName: file.name })
        }
      } else if (provider !== 'markitdown') {
        const response = await parsePlaygroundDocument(provider, file, skipImages, enrichImages, pending.signal)
        if (!pending.signal.aborted) {
          setResult({ ...response, text: response.result.markdown, warnings: response.result.warnings, fileName: file.name })
        }
      } else {
        const response = await convertPlaygroundDocument(file, pending.signal)
        if (!pending.signal.aborted) {
          setResult({ ...response, text: response.result.markdown, fileName: file.name })
        }
      }
    } catch (cause) {
      if (!pending.signal.aborted) {
        setError(cause instanceof Error ? cause.message : `${name} request failed.`)
      }
    } finally {
      controller.current = null
      setRunning(false)
    }
  }
  const download = () => {
    if (!result) {
      return
    }
    const url = URL.createObjectURL(new Blob([result.text], { type: isIndex ? 'application/json' : 'text/markdown;charset=utf-8' }))
    const link = document.createElement('a')
    link.href = url
    link.download = `${result.fileName}.${isIndex ? 'json' : 'md'}`
    document.body.appendChild(link)
    try {
      link.click()
    } finally {
      link.remove()
      window.setTimeout(() => URL.revokeObjectURL(url), 1000)
    }
  }
  return <section className="card decision-playground">
    <div className="card-head decision-toolbar">
      <h2><FileText size={18} />{name} playground</h2>
      {tab.headerActions}
    </div>
    <div className="card-body stack">
      <p className="decision-description">{isIndex ? 'Build a document tree from PDF or Markdown. Indexing and summaries may incur model charges. Token usage is returned in the response only.' : isParser ? `Convert a ${provider.toUpperCase()} document to Markdown using DocumentParsers. Enable Enrich images for AI descriptions and OCR; provider charges may apply.` : 'Convert a document to Markdown using the configured MarkItDown service.'}</p>
      <div className="playground-request-boundary">
        {tab.tabBar}
        <div className="stack playground-request-content" role="tabpanel" id={tab.panelId} aria-labelledby={tab.tabId}>
          {error ? <ErrorBanner message={error} /> : null}
          <div className="document-playground-layout">
            <div className="decision-panel">
              <div className="decision-panel-heading playground-request-heading">
                <h3>Document</h3>
                <div className="row">
                  <button className="primary" disabled={running || !file} onClick={() => void run()}><Play size={15} />{running ? 'Processing...' : action}</button>
                  {running ? <button onClick={() => {
                    controller.current?.abort()
                    setError('Request cancelled. The service may already have processed it.')
                  }}><Square size={15} />Cancel</button> : null}
                  <button disabled={running} onClick={() => {
                    setFile(null)
                    setResult(null)
                    setError('')
                    setIncludeText(true)
                    setIncludeSummaries(false)
                    setSkipImages(true)
                    setEnrichImages(false)
                    setPreview(false)
                  }}><RotateCcw size={15} />Reset</button>
                  <button disabled={running} onClick={() => input.current?.click()}><Upload size={14} />Select file</button>
                </div>
              </div>
              <input ref={input} type="file" hidden accept={accept} disabled={running} aria-label={`Select document for ${name}`} onChange={event => {
                select(event.target.files)
                event.target.value = ''
              }} />
              <div className={`image-playground-drop document-playground-drop${dragging ? ' is-dragging' : ''}`} aria-busy={running}
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
                <FileText size={36} aria-hidden="true" /><p>{dragging ? 'Drop document here' : file?.name ?? 'Drag a document here or select a file'}</p>
                <small className="hint">{file ? `${file.size.toLocaleString()} bytes · ` : ''}{isIndex ? 'PDF or UTF-8 Markdown' : isParser ? `${provider.toUpperCase()} files` : 'PDF, Office, text, HTML, and other formats supported by the service'} · Maximum 25 MB</small>
              </div>
              {isIndex ? <div className="row document-playground-options">
                <label><input type="checkbox" checked={includeText} disabled={running} onChange={event => { setIncludeText(event.target.checked); setResult(null) }} />Include text</label>
                <label><input type="checkbox" checked={includeSummaries} disabled={running} onChange={event => { setIncludeSummaries(event.target.checked); setResult(null) }} />Include summaries</label>
              </div> : null}
              {isParser ? <div className="row document-playground-options">
                <label><input type="checkbox" checked={skipImages} disabled={running} onChange={event => {
                  setSkipImages(event.target.checked)
                  if (event.target.checked) {
                    setEnrichImages(false)
                  }
                  setResult(null)
                }} />Skip images (keep placeholder comments)</label>
                <label><input type="checkbox" checked={enrichImages} disabled={running || skipImages} onChange={event => {
                  setEnrichImages(event.target.checked)
                  setResult(null)
                }} />Enrich images (description and OCR)</label>
              </div> : null}
            </div>
            <div className="decision-panel" aria-busy={running}>
              <div className="decision-panel-heading"><h3>{isIndex ? 'Document tree JSON' : 'Markdown'}</h3>{result ? <div className="row">
                {!isIndex ? <button aria-pressed={preview} onClick={() => setPreview(value => !value)}>{preview ? 'Source' : 'Preview'}</button> : null}
                <CopyButton value={result.text} /><button onClick={download} title="Download result"><Download size={14} /></button>
              </div> : null}</div>
              {result ? <div className="document-playground-result">
                {isIndex ? <JsonCode value={result.text} /> : preview
                  ? <div className="markdown"><ReactMarkdown remarkPlugins={[remarkGfm]} skipHtml>{result.text}</ReactMarkdown></div>
                  : <SourceCode path="result.md" value={result.text} />}
              </div> : <div className="decision-response-empty" role="status"><p>{running ? 'Processing document...' : `Select a document and click ${action}.`}</p></div>}
            </div>
          </div>
          {result?.warnings?.length ? <section aria-label="Parser warnings"><h3>Parser warnings</h3><ul>
            {result.warnings.map((warning, index) => <li key={index}><strong>{warning.code}:</strong> {warning.message}</li>)}
          </ul></section> : null}
          {result ? <section className="decision-summary" aria-label="Document processing summary"><h3>Summary</h3><dl>
            {result.tree?.usage ? <>
              <div><dt>Model</dt><dd>{result.tree.usage.model_id ?? 'Unavailable'}</dd></div>
              <div><dt>Input tokens</dt><dd>{result.tree.usage.input_tokens.toLocaleString()}</dd></div>
              <div><dt>Output tokens</dt><dd>{result.tree.usage.output_tokens.toLocaleString()}</dd></div>
              <div><dt>Total tokens</dt><dd>{result.tree.usage.total_tokens.toLocaleString()}</dd></div>
              <div><dt>Calls without usage</dt><dd>{result.tree.usage.calls_without_usage}</dd></div>
            </> : null}
            <div><dt>Duration</dt><dd>{result.elapsedMs.toLocaleString()} ms</dd></div>
            <div className="decision-trace"><dt>Trace ID</dt><dd>{result.traceId ?? 'Unavailable'}{result.traceId ? <CopyButton value={result.traceId} /> : null}</dd></div>
          </dl></section> : null}
        </div>
      </div>
    </div>
  </section>
}
