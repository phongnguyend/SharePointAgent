import { useEffect, useId, useRef, useState } from 'react'
import { FlaskConical, Play, RotateCcw, Square } from 'lucide-react'
import { PlaygroundRequestTabs, type PlaygroundRequestTab } from '../components/PlaygroundRequestTabs'
import { evaluateDecision, type DecisionEvaluation, type DecisionProvider } from '../api/decisionClient'
import { CopyButton, ErrorBanner } from '../components/ui'
import { JsonCode, JsonEditor } from '../components/JsonCode'

const sample = JSON.stringify({
  state: 'Checkout is unavailable for all customers. This is the third report today.',
  questions: {
    team: { type: 'choice', instructions: 'Which team should handle this?', criteria: { engineering: 'Bugs and outages', billing: 'Invoices and payments', support: 'General questions' } },
    severity: { type: 'score', instructions: 'How severe is this incident?', criteria: ['Minor', 'Major', 'Critical'] },
    urgent: { type: 'noul', instructions: 'Does this require immediate attention?' },
  },
}, null, 2)

export default function DecisionPlaygroundPage({ provider }: { provider: DecisionProvider }) {
  return <PlaygroundRequestTabs label={provider === 'jev' ? 'Jev' : 'Microsoft Decision'} active>
    {tab => <DecisionRequest provider={provider} tab={tab} />}
  </PlaygroundRequestTabs>
}

function DecisionRequest({ provider, tab }: { provider: DecisionProvider; tab: PlaygroundRequestTab }) {
  const { id: requestId, onSummary, tabBar: requestTabs, panelId, tabId } = tab
  const inputId = useId()
  const [input, setInput] = useState(sample)
  const [result, setResult] = useState<DecisionEvaluation | null>(null)
  const [error, setError] = useState('')
  const [running, setRunning] = useState(false)
  const controller = useRef<AbortController | null>(null)
  useEffect(() => () => controller.current?.abort(), [])
  useEffect(() => {
    onSummary(requestId, '', running)
  }, [requestId, running, onSummary])
  const name = provider === 'jev' ? 'Jev' : 'Microsoft Decision'

  const run = async () => {
    if (controller.current) {
      return
    }
    setError('')
    setResult(null)
    let body: unknown
    try {
      body = JSON.parse(input)
    } catch {
      setError('Enter valid JSON before running the evaluation.')
      return
    }
    const request = new AbortController()
    controller.current = request
    setRunning(true)
    try {
      const response = await evaluateDecision(provider, body, request.signal)
      if (!request.signal.aborted) {
        setResult(response)
      }
    } catch (cause) {
      if (!request.signal.aborted) {
        setError(cause instanceof Error ? cause.message : 'Evaluation failed.')
      }
    } finally {
      controller.current = null
      setRunning(false)
    }
  }

  return <section className="card decision-playground">
    <div className="card-head decision-toolbar">
      <h2><FlaskConical size={18} />{name} playground</h2>
      {tab.headerActions}
    </div>
    <div className="card-body stack">
      <p className="decision-description">Test choice, score, or yes/no (noul) with {name}. Runs may incur charges; usage is recorded under your user in SystemOne Usage.</p>
      <div className="playground-request-boundary">
      {requestTabs}
      <div className="stack playground-request-content" role="tabpanel" id={panelId} aria-labelledby={tabId}>
      <div className="decision-playground-grid">
        <div className="decision-panel">
          <div className="decision-panel-heading playground-request-heading">
            <label htmlFor={inputId}>Request JSON</label>
            <div className="row">
              <button type="button" className="primary" disabled={running || !input.trim()} onClick={() => void run()}><Play size={15} />{running ? 'Evaluating…' : 'Run evaluation'}</button>
              {running && <button type="button" onClick={() => {
                controller.current?.abort()
                setError('Request cancelled. The provider may already have processed it.')
              }}><Square size={15} />Cancel</button>}
              <button type="button" disabled={running} onClick={() => {
                setInput(sample)
                setResult(null)
                setError('')
              }}><RotateCcw size={15} />Reset example</button>
              <CopyButton value={input} />
            </div>
          </div>
          <JsonEditor id={inputId} value={input} disabled={running}
            onChange={value => { setInput(value); setResult(null) }} />
        </div>
        <div className="decision-panel" aria-busy={running}>
          <div className="decision-panel-heading"><h3>Response JSON</h3>{result ? <CopyButton value={JSON.stringify(result.result.answers, null, 2)} /> : <span className="muted">{running ? 'Running' : 'Ready'}</span>}</div>
          {result ? <JsonCode value={JSON.stringify(result.result.answers, null, 2)} /> : <div className="decision-response-empty" role="status">
            {error ? <ErrorBanner message={error} /> : <p>{running ? 'Waiting for the model…' : 'Run an evaluation to see the response here.'}</p>}
          </div>}
        </div>
      </div>
      {result && <section className="decision-summary" aria-label="Evaluation summary">
        <h3>Evaluation summary</h3>
        <dl>
          <div><dt>Model</dt><dd>{result.result.model}</dd></div>
          <div><dt>Input tokens</dt><dd>{result.result.usage.input_tokens.toLocaleString()}</dd></div>
          <div><dt>Output tokens</dt><dd>{result.result.usage.output_tokens.toLocaleString()}</dd></div>
          <div><dt>Total tokens</dt><dd>{(result.result.usage.input_tokens + result.result.usage.output_tokens).toLocaleString()}</dd></div>
          <div><dt>Duration</dt><dd>{result.elapsedMs.toLocaleString()} ms</dd></div>
          <div className="decision-trace"><dt>Trace ID</dt><dd>{result.traceId ?? 'Unavailable'}{result.traceId && <CopyButton value={result.traceId} />}</dd></div>
        </dl>
      </section>}
      </div>
      </div>
    </div>
  </section>
}
