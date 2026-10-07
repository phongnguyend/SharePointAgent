import { useState } from 'react'
import { Eye, Filter, Image, RefreshCw, RotateCcw } from 'lucide-react'
import { getImageDescriptionUsage } from '../api/client'
import type { ImageDescriptionUsageFilter, ImageDescriptionUsageReport as Report } from '../api/types'
import { useAsync } from '../lib/useAsync'
import { CopyButton, Empty, ErrorBanner, LoadingBar, Modal, Pagination, StatTile } from './ui'

function initialFilters(): ImageDescriptionUsageFilter {
  const today = new Date().toISOString().slice(0, 10)
  return { from: `${today.slice(0, 7)}-01`, to: today, model: '', user: '', attachmentId: '' }
}
const number = (value: number | null) => value == null ? 'Not reported' : value.toLocaleString()

export default function ImageDescriptionUsageReport() {
  const [filters, setFilters] = useState(initialFilters)
  const [draft, setDraft] = useState(filters)
  const [skip, setSkip] = useState(0)
  const [selected, setSelected] = useState<Report['items'][number] | null>(null)
  const report = useAsync(signal => getImageDescriptionUsage(filters, skip, signal), [filters, skip])
  const data = report.data
  const apply = (next: ImageDescriptionUsageFilter) => {
    setDraft(next)
    setFilters(next)
    setSkip(0)
  }
  const peak = Math.max(1, ...(data?.daily.map(row => row.totalTokens) ?? []))

  return <div className="stack embedding-page">
    <div className="row spread"><span className="muted">Image descriptions are tracked independently from chat turns and count toward monthly limits. Historical calls may also be included in older chat totals.</span><button disabled={report.loading} onClick={report.reload}><RefreshCw size={14} />Refresh</button></div>
    <form className="card embedding-filters chat-token-filters" onSubmit={event => {
      event.preventDefault()
      apply({ ...draft })
    }}>
      <label>From (UTC)<input type="date" required max={draft.to} value={draft.from} onChange={event => setDraft({ ...draft, from: event.target.value })} /></label>
      <label>Through (UTC)<input type="date" required min={draft.from} value={draft.to} onChange={event => setDraft({ ...draft, to: event.target.value })} /></label>
      <label>Model ID<input type="text" placeholder="All models" value={draft.model} onChange={event => setDraft({ ...draft, model: event.target.value })} /></label>
      <label>User<input type="text" maxLength={200} placeholder="Name, email, or user ID" value={draft.user} onChange={event => setDraft({ ...draft, user: event.target.value })} /></label>
      <label>Attachment ID<input type="text" placeholder="Exact attachment GUID" value={draft.attachmentId} onChange={event => setDraft({ ...draft, attachmentId: event.target.value })} /></label>
      <div className="row embedding-filter-actions"><button disabled={report.loading}><Filter size={14} />Apply filters</button><button type="button" onClick={() => apply(initialFilters())}><RotateCcw size={14} />Reset</button></div>
    </form>
    <LoadingBar active={report.loading} />
    {report.error && <ErrorBanner message={report.error} onRetry={report.reload} />}
    {data && !report.error && <div className="stack" aria-busy={report.loading}>
      <div className="tiles token-report-tiles">
        <StatTile label="Image descriptions" value={number(data.summary.calls)} icon={<Image size={15} />} />
        <StatTile label="Input tokens" value={number(data.summary.inputTokens)} />
        <StatTile label="Output tokens" value={number(data.summary.outputTokens)} />
        <StatTile label="Total reported tokens" value={number(data.summary.totalTokens)} />
        <StatTile label="Usage not reported" value={number(data.summary.unknownUsage)} hint="Calls excluded from reported token totals" />
      </div>
      {data.summary.calls === 0 ? <Empty title="No image descriptions" detail="Try another date range or clear the filters." /> : <>
        <section className="card"><div className="card-head"><h2>Daily image description tokens</h2><span className="hint">UTC · Select a day to filter</span></div><div className="card-body">
          <div className="embedding-chart" role="group" aria-label="Daily image description tokens">{data.daily.map(row => <button className="embedding-bar" key={row.day} title={`${row.day.slice(0, 10)}: ${number(row.totalTokens)} tokens, ${row.calls} calls`} aria-label={`Filter to ${row.day.slice(0, 10)}: ${number(row.totalTokens)} tokens`} onClick={() => apply({ ...filters, from: row.day.slice(0, 10), to: row.day.slice(0, 10) })}><span style={{ height: `${Math.max(2, row.totalTokens / peak * 100)}%`, opacity: row.totalTokens ? 1 : 0.2 }} /></button>)}</div>
          <div className="row spread muted"><span>{filters.from}</span><span>{filters.to}</span></div>
        </div></section>
        <div className="embedding-panels">{(['daily', 'models'] as const).map(group => <section className="card" key={group}><div className="card-head"><h2>{group === 'daily' ? 'Daily totals' : 'By model'}</h2></div><div className="table-scroll"><table><thead><tr><th>{group === 'daily' ? 'Day (UTC)' : 'Model'}</th><th>Calls</th><th>Input</th><th>Output</th><th>Total</th><th>Unknown</th></tr></thead><tbody>{(group === 'daily' ? data.daily.map(row => ({ ...row, key: row.day, label: row.day.slice(0, 10) })) : data.models.map(row => ({ ...row, key: row.modelId, label: row.modelId }))).map(row => <tr key={row.key}><td>{row.label}</td><td>{number(row.calls)}</td><td>{number(row.inputTokens)}</td><td>{number(row.outputTokens)}</td><td>{number(row.totalTokens)}</td><td>{number(row.unknownUsage)}</td></tr>)}</tbody></table></div></section>)}</div>
        <section className="card"><div className="card-head"><h2>Description calls</h2></div><div className="table-scroll"><table><thead><tr><th>Time (UTC)</th><th>Image</th><th>User</th><th>Model</th><th>Input</th><th>Output</th><th>Total</th><th /></tr></thead><tbody>{data.items.map(row => <tr key={row.id}><td>{row.createdAtUtc.replace('T', ' ').slice(0, 19)}</td><td>{row.fileName ?? row.attachmentId}</td><td title={row.userEmail ?? row.userId ?? undefined}>{row.userName ?? row.userId ?? 'Unattributed'}</td><td>{row.modelId}</td><td>{number(row.inputTokens)}</td><td>{number(row.outputTokens)}</td><td>{number(row.totalTokens)}</td><td><button onClick={() => setSelected(row)}><Eye size={14} />Details</button></td></tr>)}</tbody></table></div><Pagination skip={skip} top={25} total={data.summary.calls} onSkip={setSkip} /></section>
      </>}
    </div>}
    <Modal open={selected != null} title="Image description usage" icon={<Image size={18} />} onClose={() => setSelected(null)}>
      {selected && <div className="stack">
        <dl className="embedding-details">{Object.entries(selected).filter(([key]) => !['systemPrompt', 'prompt', 'description'].includes(key)).map(([key, value]) => <div key={key}><dt>{key.replace(/([a-z])([A-Z])/g, '$1 $2')}</dt><dd><span>{value == null ? 'Not available' : String(value)}</span>{typeof value === 'string' && <CopyButton value={value} />}</dd></div>)}</dl>
        {([['System prompt', selected.systemPrompt], ['Text sent to model', selected.prompt], ['Returned description', selected.description]] as const).map(([label, value]) => <section key={label} className="card">
          <div className="card-head"><h2>{label}</h2>{value ? <CopyButton value={value} /> : null}</div>
          <div className="card-body" style={{ whiteSpace: 'pre-wrap', overflowWrap: 'anywhere', maxHeight: 300, overflowY: 'auto' }}>{value === null ? 'Not recorded for this call.' : value === '' ? 'Empty response.' : value}</div>
        </section>)}
      </div>}
    </Modal>
  </div>
}
