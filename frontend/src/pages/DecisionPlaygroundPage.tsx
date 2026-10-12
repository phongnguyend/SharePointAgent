import { useEffect, useRef, useState } from 'react'
import { FlaskConical, Play, RotateCcw, Square } from 'lucide-react'
import { evaluateDecision, type DecisionEvaluation, type DecisionProvider } from '../api/decisionClient'
import { ErrorBanner } from '../components/ui'

const sample = JSON.stringify({
  state: 'Checkout is unavailable for all customers. This is the third report today.',
  questions: {
    team: { type: 'choice', instructions: 'Which team should handle this?', criteria: { engineering: 'Bugs and outages', billing: 'Invoices and payments', support: 'General questions' } },
    severity: { type: 'score', instructions: 'How severe is this incident?', criteria: ['Minor', 'Major', 'Critical'] },
    urgent: { type: 'noul', instructions: 'Does this require immediate attention?' },
  },
}, null, 2)

export default function DecisionPlaygroundPage({ provider }: { provider: DecisionProvider }) {
  const [input, setInput] = useState(sample)
  const [result, setResult] = useState<DecisionEvaluation | null>(null)
  const [error, setError] = useState('')
  const [running, setRunning] = useState(false)
  const controller = useRef<AbortController | null>(null)
  useEffect(() => () => controller.current?.abort(), [])
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

  return <section className="card">
    <div className="card-head"><h2><FlaskConical size={18} />{name} playground</h2></div>
    <div className="card-body stack">
      <p>Test choice, score, and yes/no (noul) questions using the server-configured model. Each run sends the input to {name} and may incur token charges.</p>
      <div className="decision-playground-grid">
        <div className="stack">
          <label htmlFor={`decision-input-${provider}`}>Request JSON</label>
          <textarea id={`decision-input-${provider}`} className="decision-json" value={input} maxLength={100000}
            spellCheck={false} disabled={running} onChange={event => { setInput(event.target.value); setResult(null) }} />
          <div className="row">
            <button type="button" className="primary" disabled={running || !input.trim()} onClick={() => void run()}><Play size={15} />{running ? 'Evaluating…' : 'Run evaluation'}</button>
            {running && <button type="button" onClick={() => { controller.current?.abort(); setError('Request cancelled. The provider may already have processed it.') }}><Square size={15} />Cancel</button>}
            <button type="button" disabled={running} onClick={() => { setInput(sample); setResult(null); setError('') }}><RotateCcw size={15} />Reset example</button>
          </div>
          <small>{provider === 'jev' ? 'Usage is recorded under your user in the Jev usage report.' : 'Token usage is returned here; it is not saved in the usage report.'}</small>
        </div>
        <div className="stack" aria-live="polite" aria-busy={running}>
          <h3>Result</h3>
          {error && <ErrorBanner message={error} />}
          {!result && !error && <p>{running ? 'Waiting for the model…' : 'Run an evaluation to view decisions and token usage.'}</p>}
          {result && <>
            <dl className="detail-grid">
              <dt>Model</dt><dd>{result.result.model}</dd>
              <dt>Input tokens</dt><dd>{result.result.usage.input_tokens.toLocaleString()}</dd>
              <dt>Output tokens</dt><dd>{result.result.usage.output_tokens.toLocaleString()}</dd>
              <dt>Total tokens</dt><dd>{(result.result.usage.input_tokens + result.result.usage.output_tokens).toLocaleString()}</dd>
              <dt>Duration</dt><dd>{result.elapsedMs.toLocaleString()} ms</dd>
              <dt>Trace ID</dt><dd>{result.traceId ?? 'Unavailable'}</dd>
            </dl>
            <pre className="decision-json decision-result">{JSON.stringify(result.result.answers, null, 2)}</pre>
          </>}
        </div>
      </div>
    </div>
  </section>
}
