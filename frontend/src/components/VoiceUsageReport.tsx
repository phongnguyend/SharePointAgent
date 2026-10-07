import { useState } from 'react'
import { Filter, Mic, RefreshCw, RotateCcw } from 'lucide-react'
import { getVoiceUsage } from '../api/client'
import type { VoiceUsageFilter } from '../api/types'
import { useAsync } from '../lib/useAsync'
import { Empty, ErrorBanner, LoadingBar, Pagination, StatTile } from './ui'

function initialFilters(): VoiceUsageFilter {
  const today = new Date().toISOString().slice(0, 10)
  return { from: `${today.slice(0, 7)}-01`, to: today, model: '', user: '' }
}

const number = (value: number | null) => value == null ? 'Not reported' : value.toLocaleString()

const size = (bytes: number) => bytes >= 1024 * 1024 ? `${(bytes / 1024 / 1024).toFixed(1)} MB` : `${Math.max(1, Math.round(bytes / 1024))} KB`

const duration = (seconds: number | null) => seconds == null || seconds === 0
  ? 'Not reported'
  : seconds < 60 ? `${seconds.toFixed(1)} s` : `${Math.floor(seconds / 60)} min ${Math.round(seconds % 60)} s`

/** Dictation transcriptions from the chat composer: tokens, audio size, and calls by day, model, and user. */
export default function VoiceUsageReport() {
  const [filters, setFilters] = useState(initialFilters)
  const [draft, setDraft] = useState(filters)
  const [skip, setSkip] = useState(0)
  const report = useAsync(signal => getVoiceUsage(filters, skip, signal), [filters, skip])
  const data = report.data
  const apply = (next: VoiceUsageFilter) => {
    setDraft(next)
    setFilters(next)
    setSkip(0)
  }
  const peak = Math.max(1, ...(data?.daily.map(row => row.totalTokens) ?? []))

  return <div className="stack embedding-page">
    <div className="row spread">
      <span className="muted">Voice dictation in the chat composer. Transcription tokens count toward each user's monthly limit; recordings and transcripts are not stored.</span>
      <button disabled={report.loading} onClick={report.reload}><RefreshCw size={14} />Refresh</button>
    </div>
    <form className="card embedding-filters chat-token-filters" onSubmit={event => {
      event.preventDefault()
      apply({ ...draft })
    }}>
      <label>From (UTC)<input type="date" required max={draft.to} value={draft.from} onChange={event => setDraft({ ...draft, from: event.target.value })} /></label>
      <label>Through (UTC)<input type="date" required min={draft.from} value={draft.to} onChange={event => setDraft({ ...draft, to: event.target.value })} /></label>
      <label>Model ID<input type="text" placeholder="All models" value={draft.model} onChange={event => setDraft({ ...draft, model: event.target.value })} /></label>
      <label>User<input type="text" maxLength={200} placeholder="Name, email, or user ID" value={draft.user} onChange={event => setDraft({ ...draft, user: event.target.value })} /></label>
      <div className="row embedding-filter-actions">
        <button disabled={report.loading}><Filter size={14} />Apply filters</button>
        <button type="button" onClick={() => apply(initialFilters())}><RotateCcw size={14} />Reset</button>
      </div>
    </form>
    <LoadingBar active={report.loading} />
    {report.error && <ErrorBanner message={report.error} onRetry={report.reload} />}
    {data && !report.error && <div className="stack" aria-busy={report.loading}>
      <div className="tiles token-report-tiles">
        <StatTile label="Transcriptions" value={number(data.summary.calls)} icon={<Mic size={15} />} />
        <StatTile label="Input tokens" value={number(data.summary.inputTokens)} hint="Audio and prompt tokens" />
        <StatTile label="Output tokens" value={number(data.summary.outputTokens)} hint="Transcript tokens" />
        <StatTile label="Total reported tokens" value={number(data.summary.totalTokens)} />
        <StatTile label="Audio received" value={size(data.summary.audioBytes)} />
        <StatTile label="Usage not reported" value={number(data.summary.unknownUsage)} hint="Calls without token usage, such as Whisper deployments" />
      </div>
      {data.summary.calls === 0 ? <Empty title="No voice transcriptions" detail="Try another date range or clear the filters." /> : <>
        <section className="card">
          <div className="card-head"><h2>Daily voice tokens</h2><span className="hint">UTC · Select a day to filter</span></div>
          <div className="card-body">
            <div className="embedding-chart" role="group" aria-label="Daily voice tokens">
              {data.daily.map(row => <button className="embedding-bar" key={row.day}
                title={`${row.day}: ${number(row.totalTokens)} tokens, ${row.calls} transcriptions`}
                aria-label={`Filter to ${row.day}: ${number(row.totalTokens)} tokens`}
                onClick={() => apply({ ...filters, from: row.day, to: row.day })}>
                <span style={{ height: `${Math.max(2, row.totalTokens / peak * 100)}%`, opacity: row.totalTokens ? 1 : 0.2 }} />
              </button>)}
            </div>
            <div className="row spread muted"><span>{filters.from}</span><span>{filters.to}</span></div>
          </div>
        </section>
        <div className="embedding-panels">
          {(['daily', 'models'] as const).map(group => <section className="card" key={group}>
            <div className="card-head"><h2>{group === 'daily' ? 'Daily totals' : 'By model'}</h2></div>
            <div className="table-scroll"><table>
              <thead><tr><th>{group === 'daily' ? 'Day (UTC)' : 'Model'}</th><th>Calls</th><th>Input</th><th>Output</th><th>Total</th><th>Audio</th><th>Unknown</th></tr></thead>
              <tbody>{(group === 'daily'
                ? data.daily.map(row => ({ ...row, key: row.day, label: row.day }))
                : data.models.map(row => ({ ...row, key: row.modelId, label: row.modelId }))).map(row => <tr key={row.key}>
                <td>{row.label}</td><td>{number(row.calls)}</td><td>{number(row.inputTokens)}</td><td>{number(row.outputTokens)}</td>
                <td>{number(row.totalTokens)}</td><td>{size(row.audioBytes)}</td><td>{number(row.unknownUsage)}</td>
              </tr>)}</tbody>
            </table></div>
          </section>)}
        </div>
        <section className="card">
          <div className="card-head"><h2>Transcriptions</h2></div>
          <div className="table-scroll"><table>
            <thead><tr><th>Time (UTC)</th><th>User</th><th>Model</th><th>Audio</th><th title="Reported by the model when it bills by duration; otherwise the recording length measured in the browser.">Duration</th><th>Input</th><th>Output</th><th>Total</th></tr></thead>
            <tbody>{data.items.map(row => <tr key={row.id}>
              <td>{row.createdAtUtc.replace('T', ' ').slice(0, 19)}</td>
              <td title={row.userEmail ?? row.userId}>{row.userName ?? row.userId}</td>
              <td>{row.modelId}</td>
              <td>{size(row.audioBytes)}</td>
              <td>{duration(row.durationSeconds)}</td>
              <td>{number(row.inputTokens)}</td><td>{number(row.outputTokens)}</td><td>{number(row.totalTokens)}</td>
            </tr>)}</tbody>
          </table></div>
          <Pagination skip={skip} top={25} total={data.summary.calls} onSkip={setSkip} />
        </section>
      </>}
    </div>}
  </div>
}
