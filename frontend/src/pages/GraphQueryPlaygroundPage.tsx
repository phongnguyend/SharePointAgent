import { PlaygroundRequestTabs, type PlaygroundRequestTab } from '../components/PlaygroundRequestTabs'
import { useEffect, useId, useRef, useState } from 'react'
import { Network, Play, RotateCcw, Square } from 'lucide-react'
import { request } from '../api/http'
import type { GraphQueryPlan } from '../api/graphClient'
import { ErrorBanner } from '../components/ui'
import { GraphUsageSummary, type GraphModelUsage } from '../components/GraphUsageSummary'
import { JsonCodeBlock } from '../components/JsonCode'
import './GraphQueryPlaygroundPage.css'

type PlanResult = { plan: GraphQueryPlan; elapsedMs: number; usage: GraphModelUsage[] }

export default function GraphQueryPlaygroundPage() {
  return <PlaygroundRequestTabs label="Query plan" active>
    {tab => <GraphQueryRequest tab={tab} />}
  </PlaygroundRequestTabs>
}

function GraphQueryRequest({ tab }: { tab: PlaygroundRequestTab }) {
  const { id, onSummary } = tab
  const inputId = useId()
  const limit = 2000
  const [question, setQuestion] = useState('')
  const [result, setResult] = useState<PlanResult | null>(null)
  const [error, setError] = useState('')
  const [running, setRunning] = useState(false)
  const controller = useRef<AbortController | null>(null)
  useEffect(() => () => controller.current?.abort(), [])
  useEffect(() => {
    onSummary(id, '', running)
  }, [id, onSummary, running])

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

  return <section className="card graph-plan-playground">
    <div className="card-head decision-toolbar">
      <h2><Network size={18} />Query plan playground</h2>
    </div>
    <div className="card-body stack">
      <p className="decision-description">Extract entity names and relationship types from a question without graph retrieval.</p>
      <div className="playground-request-boundary">
        {tab.tabBar}
        <div className="stack playground-request-content" role="tabpanel" id={tab.panelId} aria-labelledby={tab.tabId}>
          {error && <ErrorBanner message={error} />}
          <div className="decision-playground-grid">
            <div className="decision-panel graph-plan-editor">
              <div className="decision-panel-heading playground-request-heading">
                <label htmlFor={inputId}>Question</label>
                <div className="row">
                  <button type="button" className="primary" disabled={running || !question.trim()} onClick={() => void run()}><Play size={15} />Extract plan</button>
                  {running && <button type="button" onClick={() => controller.current?.abort()}><Square size={15} />Cancel</button>}
                  <button type="button" disabled={running} onClick={() => { setQuestion(''); setResult(null); setError('') }}><RotateCcw size={15} />Reset</button>
                  <span className="muted">{question.length.toLocaleString()} / {limit.toLocaleString()}</span>
                </div>
              </div>
              <textarea id={inputId} className="graph-plan-textarea" value={question} onChange={event => { setQuestion(event.target.value); setResult(null) }}
                maxLength={limit} disabled={running} placeholder="Which systems depend on the customer portal?" />
            </div>
            <section className="decision-panel graph-plan-result" aria-busy={running}>
              <div className="decision-panel-heading">
                <h3>Query plan</h3>
              </div>
              {!result ? <div className="decision-response-empty" role="status" aria-live="polite">{running ? 'Extracting?' : 'Enter question and click Extract plan.'}</div>
                : <div className="graph-plan-output">
                  <p><strong>Entities:</strong> {result.plan.entities.join(', ') || 'None extracted'}</p>
                  <p><strong>Relationship types:</strong> {result.plan.relationshipTypes.join(', ') || 'All configured relationship types'}</p>
                  <GraphUsageSummary usage={result.usage} />
                  <JsonCodeBlock value={JSON.stringify(result.plan, null, 2)} />
                  <p className="muted" role="status">Completed in {result.elapsedMs.toLocaleString()} ms</p>
                </div>}
            </section>
          </div>
        </div>
      </div>
    </div>
  </section>
}
