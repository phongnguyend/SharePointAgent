import { useEffect, useId, useRef, useState } from 'react'
import { Network, Play, RotateCcw, Square } from 'lucide-react'
import { request } from '../api/http'
import { ErrorBanner } from '../components/ui'
import { GraphUsageSummary, type GraphModelUsage } from '../components/GraphUsageSummary'
import { JsonCodeBlock } from '../components/JsonCode'
import './GraphExtractionPlaygroundPage.css'

type ExtractionResult = { chunks: unknown[]; elapsedMs: number; usage: GraphModelUsage[] }

export default function GraphExtractionPlaygroundPage() {
  const inputId = useId()
  const limit = 20000
  const [text, setText] = useState('')
  const [result, setResult] = useState<ExtractionResult | null>(null)
  const [error, setError] = useState('')
  const [running, setRunning] = useState(false)
  const controller = useRef<AbortController | null>(null)
  useEffect(() => () => controller.current?.abort(), [])

  const run = async () => {
    if (controller.current || !text.trim()) {
      return
    }
    const pending = new AbortController()
    controller.current = pending
    setRunning(true)
    setError('')
    setResult(null)
    try {
      const response = await request<ExtractionResult>('/api/admin/graph-extraction-playground', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ text: text }), signal: pending.signal,
      })
      if (!pending.signal.aborted) {
        setResult(response)
      }
    } catch (cause) {
      setError(pending.signal.aborted ? 'Extraction cancelled.' : cause instanceof Error ? cause.message : 'Entity and relationship extraction failed.')
    } finally {
      controller.current = null
      setRunning(false)
    }
  }

  return <section className="card graph-extraction-playground">
    <div className="card-head decision-toolbar">
      <h2><Network size={18} />Entity and relationship extraction</h2>
      <div className="row">
        <button type="button" className="primary" disabled={running || !text.trim()} onClick={() => void run()}><Play size={15} />Extract</button>
        {running && <button type="button" onClick={() => controller.current?.abort()}><Square size={15} />Cancel</button>}
        <button type="button" disabled={running} onClick={() => { setText(''); setResult(null); setError('') }}><RotateCcw size={15} />Reset</button>
      </div>
    </div>
    <div className="card-body stack">
      <p className="decision-description">Test indexing extraction on document text, including chunking and grounding. Nothing is saved to the graph.</p>
      {error && <ErrorBanner message={error} />}
      <div className="decision-playground-grid">
        <div className="decision-panel graph-extraction-editor">
          <div className="decision-panel-heading">
            <label htmlFor={inputId}>Document text</label>
            <span className="muted">{text.length.toLocaleString()} / {limit.toLocaleString()}</span>
          </div>
          <textarea id={inputId} className="graph-extraction-textarea" value={text} onChange={event => { setText(event.target.value); setResult(null) }}
            maxLength={limit} disabled={running} placeholder="The CRM depends on ERP for invoicing." />
        </div>
        <section className="decision-panel graph-extraction-result" aria-busy={running}>
          <div className="decision-panel-heading">
            <h3>Extraction results</h3>
          </div>
          {!result ? <div className="decision-response-empty" role="status" aria-live="polite">{running ? 'Extracting?' : 'Enter document text and click Extract.'}</div>
            : <div className="graph-extraction-output">
              <p className="muted">Raw and grounded entities and relationships are shown per chunk, with rejection counts.</p>
              <GraphUsageSummary usage={result.usage} />
              <JsonCodeBlock value={JSON.stringify(result.chunks, null, 2)} />
              <p className="muted" role="status">Completed in {result.elapsedMs.toLocaleString()} ms</p>
            </div>}
        </section>
      </div>
    </div>
  </section>
}
