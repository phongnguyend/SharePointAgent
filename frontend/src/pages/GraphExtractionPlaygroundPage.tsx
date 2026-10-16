import { useEffect, useRef, useState } from 'react'
import { Network, Play, RotateCcw, Square } from 'lucide-react'
import { request } from '../api/http'
import { CopyButton, ErrorBanner } from '../components/ui'
import './GraphExtractionPlaygroundPage.css'

type ExtractionResult = { chunks: unknown[]; elapsedMs: number }

export default function GraphExtractionPlaygroundPage() {
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
    <div className="card-head"><h2><Network size={18} />{'Entity and relationship extraction'}</h2></div>
    <div className="card-body stack">
      <p className="muted">{'Test indexing extraction on document text, including chunking and grounding. Nothing is saved to the graph.'}</p>
      <form className="stack" onSubmit={event => { event.preventDefault(); void run() }}>
        <label className="graph-extraction-text">{'Document text'}<textarea value={text} onChange={event => setText(event.target.value)} maxLength={limit} rows={6} disabled={running} placeholder={'The CRM depends on ERP for invoicing.'} required /></label>
        <div className="graph-extraction-actions">
          <button className="primary" type="submit" disabled={running || !text.trim()}><Play size={15} /> {'Extract entities and relationships'}</button>
          {running ? <button type="button" onClick={() => controller.current?.abort()}><Square size={15} />Cancel</button>
            : <button type="button" onClick={() => { setText(''); setResult(null); setError('') }}><RotateCcw size={15} />Reset</button>}
          <span className="muted">{text.length.toLocaleString()} / {limit.toLocaleString()}</span>
        </div>
      </form>
      {error && <ErrorBanner message={error} />}
      <div role="status" aria-live="polite">{running ? 'Extracting…' : result ? `Completed in ${result.elapsedMs.toLocaleString()} ms` : 'Enter document text to begin.'}</div>
      {result && <section className="decision-panel">
        <div className="decision-panel-heading"><h3>{'Extraction results'}</h3><CopyButton value={JSON.stringify(result.chunks, null, 2)} /></div>
        <div className="graph-extraction-output">
          <p>Raw model output and grounded entities/relationships are shown for each chunk, including rejection counts.</p>
          <pre>{JSON.stringify(result.chunks, null, 2)}</pre>
        </div>
      </section>}
    </div>
  </section>
}
