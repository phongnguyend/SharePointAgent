import { useEffect, useId, useRef, useState } from 'react'
import { PlaygroundRequestTabs, type PlaygroundRequestTab } from '../components/PlaygroundRequestTabs'
import { Play, RefreshCw, RotateCcw, ShieldCheck, Square } from 'lucide-react'
import { evaluateSafety, getSafetyOptions, type SafetyAssessment, type SafetyOptions } from '../api/contentSafetyPlaygroundClient'
import { CopyButton, ErrorBanner } from '../components/ui'

export default function ContentSafetyPlaygroundPage({ active }: { active: boolean }) {
  return <PlaygroundRequestTabs label="Content Safety" active={active}>
    {tab => <ContentSafetyRequest tab={tab} />}
  </PlaygroundRequestTabs>
}

function ContentSafetyRequest({ tab }: { tab: PlaygroundRequestTab }) {
  const { active, id, onSummary } = tab
  const inputId = useId()
  const [options, setOptions] = useState<SafetyOptions | null>(null)
  const [retry, setRetry] = useState(0)
  const [text, setText] = useState('')
  const [running, setRunning] = useState(false)
  useEffect(() => {
    onSummary(id, '', running)
  }, [id, onSummary, running])
  const [error, setError] = useState('')
  const [result, setResult] = useState<SafetyAssessment | null>(null)
  const pending = useRef<AbortController | null>(null)
  useEffect(() => () => pending.current?.abort(), [])
  useEffect(() => {
    if (!active) {
      return
    }
    const controller = new AbortController()
    setError('')
    void getSafetyOptions(controller.signal).then(setOptions).catch(cause => {
      if (!controller.signal.aborted) {
        setError(cause instanceof Error ? cause.message : 'Could not load content safety configuration.')
      }
    })
    return () => controller.abort()
  }, [active, retry])

  const run = async () => {
    if (!options?.configured || !text.trim() || text.length > options.maxCharacters || pending.current) {
      return
    }
    const controller = new AbortController()
    pending.current = controller
    setRunning(true)
    setError('')
    setResult(null)
    try {
      const response = await evaluateSafety(text, controller.signal)
      if (!controller.signal.aborted) {
        setResult(response)
      }
    } catch (cause) {
      setError(controller.signal.aborted ? 'Request cancelled. The provider may already have processed it.'
        : cause instanceof Error ? cause.message : 'Content safety analysis failed.')
    } finally {
      pending.current = null
      setRunning(false)
    }
  }

  return <section className="card safety-playground">
    <div className="card-head decision-toolbar">
      <h2><ShieldCheck size={18} />Content Safety</h2>
    </div>
    <div className="card-body stack">
      <p className="decision-description">Analyze text against the configured safety thresholds. Usage is recorded under your user; provider charges may apply.</p>
      <div className="playground-request-boundary">
        {tab.tabBar}
        <div className="stack playground-request-content" role="tabpanel" id={tab.panelId} aria-labelledby={tab.tabId}>
          {error && <ErrorBanner message={error} />}
          {!options ? <p role="status">Loading configuration…</p> : !options.configured && <p role="status">Configure the ContentSafety endpoint and credentials on the server to analyze text.</p>}
          <div className="decision-playground-grid">
            <div className="decision-panel safety-playground-editor">
              <div className="decision-panel-heading playground-request-heading">
                <label htmlFor={inputId}>Text to analyze</label>
                <div className="row">
                  <button type="button" className="primary" disabled={running || !options?.configured || !text.trim()} onClick={() => void run()}><Play size={15} />Analyze</button>
                  {running && <button type="button" onClick={() => pending.current?.abort()}><Square size={15} />Cancel</button>}
                  <button type="button" disabled={running} onClick={() => { setText(''); setResult(null); setError('') }}><RotateCcw size={15} />Reset</button>
                  <button type="button" className="ghost icon-only" disabled={running} aria-label="Reload content safety availability and text limits" title="Reload content safety availability and text limits" onClick={() => setRetry(value => value + 1)}><RefreshCw size={15} /></button>
                  <span className="muted">{text.length.toLocaleString()} / {(options?.maxCharacters ?? 10000).toLocaleString()}</span>
                </div>
              </div>
              <textarea id={inputId} className="safety-playground-input" placeholder="Enter text to evaluate" value={text} maxLength={options?.maxCharacters ?? 10000} disabled={running}
                onChange={event => { setText(event.target.value); setResult(null) }} />
            </div>
            <div className="decision-panel" aria-busy={running}>
              <div className="decision-panel-heading"><h3>Assessment</h3>{result && <CopyButton value={JSON.stringify(result, null, 2)} />}</div>
              {!result ? <div className="decision-response-empty" role="status">{running ? 'Analyzing text…' : 'Enter text and click Analyze.'}</div> : <div className="safety-playground-results">
                <p><span className={`badge ${result.status === 'Blocked' ? 'critical' : 'good'}`}>{result.status}</span> by the configured policy</p>
                <p className="muted">Scores range from 0 to 7. A score at or above its threshold blocks the text.</p>
                <table><thead><tr><th>Category</th><th>Severity</th><th>Threshold</th><th>Result</th></tr></thead><tbody>
                  {result.categories.map(category => <tr key={category.category}><td>{category.category === 'SelfHarm' ? 'Self-harm' : category.category}</td><td>{category.severity} / 7</td><td>{category.threshold}</td><td>{category.severity >= category.threshold ? 'Blocked' : 'Allowed'}</td></tr>)}
                </tbody></table>
                <p className="muted">{result.characterCount.toLocaleString()} characters · {result.elapsedMs.toLocaleString()} ms</p>
                <p className="safety-playground-id">Assessment: {result.assessmentId}</p>
                {result.traceId && <p className="safety-playground-id">Trace: {result.traceId}</p>}
              </div>}
            </div>
          </div>
        </div>
      </div>
    </div>
  </section>
}
