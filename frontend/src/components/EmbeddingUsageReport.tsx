import { useState } from 'react'
import { Activity, Cpu, Eye, Filter, Layers, RefreshCw, RotateCcw, Users } from 'lucide-react'
import { getEmbeddingUsage } from '../api/client'
import type { EmbeddingUsageFilter, EmbeddingUsageGroup, EmbeddingUsageRecord } from '../api/types'
import { CopyButton, Empty, ErrorBanner, LoadingBar, Modal, Pagination, StatTile } from '../components/ui'
import { useAsync } from '../lib/useAsync'

const number = (value: number) => value.toLocaleString()
const label = (value: string) => value.replace(/([a-z])([A-Z])/g, '$1 $2')
const utcTime = (value: string) => new Date(value).toISOString().replace('T', ' ').slice(0, 19)
function initialFilters(): EmbeddingUsageFilter {
  const today = new Date().toISOString().slice(0, 10)
  return { from: `${today.slice(0, 7)}-01`, to: today, model: '', operation: '', user: '', reference: '', unattributed: false }
}

function Breakdown({ rows, onSelect }: { rows: EmbeddingUsageGroup[]; onSelect: (name: string) => void }) {
  const maximum = Math.max(1, ...rows.map(row => row.tokens))
  return <div className="embedding-breakdown">
    {rows.map(row => <button key={row.name} className="embedding-group" onClick={() => onSelect(row.name)} title={`Filter by ${row.name}`}>
      <span className="embedding-group-heading"><strong>{label(row.name)}</strong><strong>{number(row.tokens)}</strong></span>
      <span className="embedding-meter"><span style={{ width: `${row.tokens / maximum * 100}%` }} /></span>
      <span className="embedding-group-heading muted"><span>{number(row.calls)} calls</span><span>{row.unknownCalls ? `${number(row.unknownCalls)} unreported` : 'tokens'}</span></span>
    </button>)}
  </div>
}

export default function EmbeddingUsageReport() {
  const [filters, setFilters] = useState(initialFilters)
  const [draft, setDraft] = useState(initialFilters)
  const [skip, setSkip] = useState(0)
  const [tab, setTab] = useState<'activity' | 'daily' | 'users'>('activity')
  const [selected, setSelected] = useState<EmbeddingUsageRecord | null>(null)
  const report = useAsync(signal => getEmbeddingUsage(filters, skip, signal), [filters, skip])
  const data = report.data
  const apply = (next: EmbeddingUsageFilter) => {
    setFilters(next)
    setDraft(next)
    setSkip(0)
  }
  const chartDays: { day: string; tokens: number; calls: number }[] = []
  if (data) {
    const byDay = new Map(data.daily.map(day => [day.day.slice(0, 10), day]))
    const end = new Date(`${data.to}T00:00:00Z`)
    for (const day = new Date(`${data.from}T00:00:00Z`); day <= end; day.setUTCDate(day.getUTCDate() + 1)) {
      const key = day.toISOString().slice(0, 10)
      chartDays.push({ day: key, tokens: byDay.get(key)?.tokens ?? 0, calls: byDay.get(key)?.calls ?? 0 })
    }
  }
  const maximum = Math.max(1, ...chartDays.map(day => day.tokens))
  return <div className="stack embedding-page">
    <div className="row spread">
      <span className="muted">Indexing and search consumption. Dates are UTC.</span>
      <button onClick={report.reload} disabled={report.loading}><RefreshCw size={14} />Refresh</button>
    </div>
    <form className="card embedding-filters" onSubmit={event => { event.preventDefault(); apply({ ...draft }) }}>
      <label>From (UTC)<input type="date" required value={draft.from} max={draft.to} onChange={event => setDraft({ ...draft, from: event.target.value })} /></label>
      <label>Through (UTC)<input type="date" required value={draft.to} min={draft.from} onChange={event => setDraft({ ...draft, to: event.target.value })} /></label>
      <label>Embedding model<select value={draft.model} onChange={event => setDraft({ ...draft, model: event.target.value })}><option value="">All models</option>{[...new Set([...(data?.modelOptions ?? []), ...(draft.model ? [draft.model] : [])])].map(model => <option key={model}>{model}</option>)}</select></label>
      <label>Activity<select value={draft.operation} onChange={event => setDraft({ ...draft, operation: event.target.value })}><option value="">All activities</option>{[...new Set([...(data?.operationOptions ?? []), ...(draft.operation ? [draft.operation] : [])])].map(operation => <option key={operation} value={operation}>{label(operation)}</option>)}</select></label>
      <label>User<input type="text" placeholder="Name, email, or user ID" maxLength={200} value={draft.user} disabled={draft.unattributed} onChange={event => setDraft({ ...draft, user: event.target.value })} /></label>
      <label>Reference ID<input type="text" placeholder="File, attachment, question, trace…" maxLength={200} value={draft.reference} onChange={event => setDraft({ ...draft, reference: event.target.value })} /></label>
      <div className="row embedding-filter-actions">
        <label className="embedding-checkbox"><input type="checkbox" checked={draft.unattributed} onChange={event => setDraft({ ...draft, unattributed: event.target.checked, user: '' })} />Unattributed only</label>
        <button type="submit" disabled={report.loading}><Filter size={14} />Apply filters</button>
        <button type="button" onClick={() => apply(initialFilters())}><RotateCcw size={14} />Reset</button>
      </div>
    </form>
    <LoadingBar active={report.loading} />
    {report.error && <ErrorBanner message={report.error} onRetry={report.reload} />}
    {data && !report.error && <div className="stack" aria-busy={report.loading} style={{ opacity: report.loading ? 0.55 : 1, pointerEvents: report.loading ? 'none' : undefined }}>
      <div className="tiles">
        <StatTile label="Reported tokens" value={number(data.summary.tokens)} hint={`${number(data.summary.inputTokens)} reported input tokens`} icon={<Cpu size={15} />} />
        <StatTile label="Embedding calls" value={number(data.summary.calls)} hint={`${number(data.summary.unknownCalls)} calls without token counts`} icon={<Activity size={15} />} attention={data.summary.unknownCalls > 0} />
        <StatTile label="Embedding models" value={number(data.summary.models)} hint="Across the filtered period" icon={<Layers size={15} />} />
        <StatTile label="Attributed users" value={number(data.summary.users)} hint="Excludes unattributed activity" icon={<Users size={15} />} />
      </div>
      <div className="embedding-period muted">{data.from} – {data.to} UTC · Totals cover all matching calls, including reindexing. Unreported tokens are excluded; embedding tokens do not count toward chat limits.</div>
      {data.summary.calls === 0 ? <div className="card"><Empty title="No embedding activity in this range" detail="Try a wider date range or clear the filters. Usage starts when tracking is enabled." /></div> : <>
        <section className="card">
          <div className="card-head"><h2>Daily token consumption</h2><span className="hint">Peak: {number(maximum === 1 && data.summary.tokens === 0 ? 0 : maximum)} tokens / day</span></div>
          <div className="card-body">
            <div className="embedding-chart" role="group" aria-label="Daily embedding tokens. Select a bar to filter to that day.">
              {chartDays.map(day => <button key={day.day} className="embedding-bar" title={`${day.day}: ${number(day.tokens)} tokens · ${number(day.calls)} calls`} aria-label={`${day.day}: ${number(day.tokens)} tokens, ${number(day.calls)} calls. Filter to day.`} onClick={() => apply({ ...filters, from: day.day, to: day.day })}><span style={{ height: `${Math.max(2, day.tokens / maximum * 100)}%`, opacity: day.tokens ? 1 : 0.2 }} /></button>)}
            </div>
            <div className="row spread muted"><span>{data.from}</span><span>Daily values available in the Daily totals tab</span><span>{data.to}</span></div>
          </div>
        </section>
        <div className="embedding-panels">
          <section className="card"><div className="card-head"><h2>By embedding model</h2><span className="hint">Select to filter</span></div><Breakdown rows={data.models} onSelect={model => apply({ ...filters, model })} /></section>
          <section className="card"><div className="card-head"><h2>By activity</h2><span className="hint">Select to filter</span></div><Breakdown rows={data.operations} onSelect={operation => apply({ ...filters, operation })} /></section>
        </div>
        <section className="card">
          <div className="card-head embedding-tabs" role="tablist" aria-label="Usage breakdown">
            {(['activity', 'daily', 'users'] as const).map((value, index, tabs) => <button key={value} role="tab" id={`embedding-tab-${value}`} aria-controls="embedding-panel" aria-selected={tab === value} tabIndex={tab === value ? 0 : -1} className={tab === value ? 'primary' : 'ghost'} onClick={() => setTab(value)} onKeyDown={event => {
              let next = index
              if (event.key === 'ArrowRight') {
                next = (index + 1) % tabs.length
              } else if (event.key === 'ArrowLeft') {
                next = (index + tabs.length - 1) % tabs.length
              } else if (event.key === 'Home') {
                next = 0
              } else if (event.key === 'End') {
                next = tabs.length - 1
              } else {
                return
              }
              event.preventDefault()
              setTab(tabs[next])
              document.getElementById(`embedding-tab-${tabs[next]}`)?.focus()
            }}>{value === 'activity' ? 'Activity log' : value === 'daily' ? 'Daily totals' : 'Top users'}</button>)}
          </div>
          <div id="embedding-panel" role="tabpanel" aria-labelledby={`embedding-tab-${tab}`} tabIndex={0}>
            <div className="table-scroll">
              {tab === 'activity' && <table><thead><tr><th>Time (UTC)</th><th>Activity</th><th>Embedding model</th><th>User</th><th>Tokens</th><th>Source</th><th /></tr></thead><tbody>{data.items.map(({ usage, userName }) => <tr key={usage.id}>
                <td className="nowrap">{utcTime(usage.createdAtUtc)}</td><td>{label(usage.operation)}</td><td><strong>{usage.embeddingModelId}</strong><div className="muted">{usage.deploymentId}</div></td><td>{userName ?? usage.userId ?? 'Unattributed / background'}</td><td className="nowrap">{usage.totalTokens == null ? 'Not reported' : number(usage.totalTokens)}</td><td>{usage.attachmentId ? 'Attachment' : usage.fileId ? 'SharePoint file' : 'Search query'}{usage.chunkNumber != null && <div className="muted">Chunk {usage.chunkNumber}</div>}</td><td><button onClick={() => setSelected(usage)} aria-label={`View usage details for ${utcTime(usage.createdAtUtc)}`}><Eye size={14} />Details</button></td>
              </tr>)}</tbody></table>}
              {tab === 'daily' && <table><thead><tr><th>Date (UTC)</th><th>Reported tokens</th><th>Calls</th><th>Unreported calls</th></tr></thead><tbody>{data.daily.map(day => <tr key={day.day}><td>{day.day.slice(0, 10)}</td><td>{number(day.tokens)}</td><td>{number(day.calls)}</td><td>{number(day.unknownCalls)}</td></tr>)}</tbody></table>}
              {tab === 'users' && <table><thead><tr><th>User (top 100 by tokens)</th><th>Reported tokens</th><th>Calls</th><th>Unreported calls</th><th /></tr></thead><tbody>{data.users.map(user => <tr key={user.userId ?? 'background'}><td>{user.name}</td><td>{number(user.tokens)}</td><td>{number(user.calls)}</td><td>{number(user.unknownCalls)}</td><td><button onClick={() => apply({ ...filters, user: user.userId ?? '', unattributed: user.userId == null })}><Filter size={14} />Filter</button></td></tr>)}</tbody></table>}
            </div>
            {tab === 'activity' && <Pagination skip={skip} top={25} total={data.summary.calls} onSkip={setSkip} />}
          </div>
        </section>
      </>}
    </div>}
    <Modal open={selected !== null} title="Embedding call details" icon={<Cpu size={18} />} onClose={() => setSelected(null)}>
      {selected && <dl className="embedding-details">{Object.entries(selected).map(([key, value]) => <div key={key}><dt>{label(key).replace(/^./, first => first.toUpperCase())}</dt><dd><span>{value == null ? 'Not available' : String(value)}</span>{value != null && typeof value === 'string' && <CopyButton value={value} />}</dd></div>)}</dl>}
    </Modal>
  </div>
}
