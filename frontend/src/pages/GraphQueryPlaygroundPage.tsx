import { useEffect, useRef, useState } from 'react'
import { Network, Play, RotateCcw, Square } from 'lucide-react'
import { request } from '../api/http'
import type { GraphQueryPlan } from '../api/graphClient'
import { CopyButton, ErrorBanner } from '../components/ui'
import './GraphQueryPlaygroundPage.css'

type PlanResult = { plan: GraphQueryPlan; elapsedMs: number }

export default function GraphQueryPlaygroundPage() {
  const limit = 2000
  const [question, setQuestion] = useState('')
  const [result, setResult] = useState<PlanResult | null>(null)
  const [error, setError] = useState('')
  const [running, setRunning] = useState(false)
  const controller = useRef<AbortController | null>(null)
  useEffect(() => () => controller.current?.abort(), [])

  const run = async () => {
    if (controller.current || !question.trim()) {
      return
    }
    const pending = new AbortController()
    controller.current = pending
    setRunning(true)
    setError('')
    setResult(null)
    try {
      const response = await request<PlanResult>('/api/admin/graph-query-playground', {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ query: question }), signal: pending.signal,
      })
      if (!pending.signal.aborted) {
        setResult(response)
      }
    } catch (cause) {
      setError(pending.signal.aborted ? 'Query planning cancelled.' : cause instanceof Error ? cause.message : 'Query planning failed.')
    } finally {
      controller.current = null
      setRunning(false)
    }
  }

  return <section className="card graph-query-playground">
    <div className="card-head"><h2><Network size={18} />{'Query plan playground'}</h2></div>
    <div className="card-body stack">
      <p className="muted">{'Extract entity names and relationship types from a question without graph retrieval.'}</p>
      <form className="stack" onSubmit={event => { event.preventDefault(); void run() }}>
        <label className="graph-plan-question">{'Question'}<textarea value={question} onChange={event => setQuestion(event.target.value)} maxLength={limit} rows={6} disabled={running} placeholder={'Which systems depend on the customer portal?'} required /></label>
        <div className="graph-plan-actions">
          <button className="primary" type="submit" disabled={running || !question.trim()}><Play size={15} /> {'Extract plan'}</button>
          {running ? <button type="button" onClick={() => controller.current?.abort()}><Square size={15} />Cancel</button>
            : <button type="button" onClick={() => { setQuestion(''); setResult(null); setError('') }}><RotateCcw size={15} />Reset</button>}
          <span className="muted">{question.length.toLocaleString()} / {limit.toLocaleString()}</span>
        </div>
      </form>
      {error && <ErrorBanner message={error} />}
      <div role="status" aria-live="polite">{running ? 'Extracting…' : result ? `Completed in ${result.elapsedMs.toLocaleString()} ms` : 'Enter a question to begin.'}</div>
      {result && <section className="decision-panel">
        <div className="decision-panel-heading"><h3>{'Query plan'}</h3><CopyButton value={JSON.stringify(result.plan, null, 2)} /></div>
        <div className="graph-plan-output">
          <><p><strong>Entities:</strong> {result.plan.entities.join(', ') || 'None extracted'}</p>
          <p><strong>Relationship types:</strong> {result.plan.relationshipTypes.join(', ') || 'All configured relationship types'}</p></>
          <pre>{JSON.stringify(result.plan, null, 2)}</pre>
        </div>
      </section>}
    </div>
  </section>
}
