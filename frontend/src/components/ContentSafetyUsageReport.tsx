import { useState } from 'react'
import { Filter, RefreshCw, ShieldCheck, Eye } from 'lucide-react'
import { getContentSafetyUsage } from '../api/client'
import type { ContentSafetyUsageReport as Report } from '../api/types'
import { useAsync } from '../lib/useAsync'
import { CopyButton, Empty, ErrorBanner, LoadingBar, Modal, Pagination, StatTile } from './ui'

export default function ContentSafetyUsageReport() {
  const today = new Date().toISOString().slice(0, 10)
  const [filters, setFilters] = useState({ from: `${today.slice(0, 7)}-01`, to: today, status: '', operation: '' })
  const [draft, setDraft] = useState(filters)
  const [skip, setSkip] = useState(0)
  const [selected, setSelected] = useState<Report['items'][number] | null>(null)
  const report = useAsync(signal => getContentSafetyUsage(filters, skip, signal), [filters, skip])
  const data = report.data
  return <div className="stack embedding-page">
    <div className="row spread"><span className="muted">Text safety checks across prompts, responses, and attachments. UTC dates.</span><button disabled={report.loading} onClick={report.reload}><RefreshCw size={14} />Refresh</button></div>
    <form className="card embedding-filters content-safety-filters" onSubmit={event => {
      event.preventDefault()
      setFilters({ ...draft })
      setSkip(0)
    }}>
      <label>From (UTC)<input type="date" required max={draft.to} value={draft.from} onChange={event => setDraft({ ...draft, from: event.target.value })} /></label>
      <label>Through (UTC)<input type="date" required min={draft.from} value={draft.to} onChange={event => setDraft({ ...draft, to: event.target.value })} /></label>
      <label>Outcome<select value={draft.status} onChange={event => setDraft({ ...draft, status: event.target.value })}><option value="">All outcomes</option>{['Allowed', 'Blocked', 'Failed', 'Cancelled', 'Started'].map(value => <option key={value}>{value}</option>)}</select></label>
      <label>Activity<select value={draft.operation} onChange={event => setDraft({ ...draft, operation: event.target.value })}><option value="">All activities</option>{['UserMessage', 'AssistantResponse', 'AttachmentText'].map(value => <option key={value}>{value}</option>)}</select></label>
      <button type="submit" disabled={report.loading}><Filter size={14} />Apply filters</button>
    </form>
    <LoadingBar active={report.loading} />
    {report.error && <ErrorBanner message={report.error} onRetry={report.reload} />}
    {data && !report.error && <div className="stack" aria-busy={report.loading}>
      <div className="tiles">
        <StatTile label="Analysis requests" value={data.summary.requests.toLocaleString()} icon={<ShieldCheck size={15} />} hint={`${data.summary.allowed.toLocaleString()} allowed`} />
        <StatTile label="Blocked requests" value={data.summary.blocked.toLocaleString()} attention={data.summary.blocked > 0} />
        <StatTile label="Failed / cancelled" value={data.summary.failed.toLocaleString()} hint="Started requests may still be in progress" />
        <StatTile label="Estimated text records" value={data.summary.estimatedTextRecords.toLocaleString()} hint={`${data.summary.characters.toLocaleString()} characters across all attempts`} />
      </div>
      <p className="muted">Estimates use 1,000-character units for completed checks, not tokens or an Azure invoice. Long texts create multiple requests. Submitted text is not stored in this ledger.</p>
      {data.summary.requests === 0 ? <Empty title="No Content Safety usage" detail="Checks are recorded after Content Safety is configured and enabled." /> : <>
        <section className="card"><div className="card-head"><h2>Daily checks</h2></div><div className="table-scroll"><table><thead><tr><th>Day (UTC)</th><th>Requests</th><th>Blocked</th><th>Characters</th></tr></thead><tbody>{data.daily.map(row => <tr key={row.day}><td>{row.day.slice(0, 10)}</td><td>{row.requests.toLocaleString()}</td><td>{row.blocked.toLocaleString()}</td><td>{row.characters.toLocaleString()}</td></tr>)}</tbody></table></div></section>
        <section className="card"><div className="card-head"><h2>Analysis log</h2></div><div className="table-scroll"><table><thead><tr><th>Time (UTC)</th><th>Activity</th><th>Outcome</th><th>Characters</th><th>User ID</th><th /></tr></thead><tbody>{data.items.map(row => <tr key={row.id}><td>{row.createdAtUtc.replace('T', ' ').slice(0, 19)}</td><td>{row.operation}</td><td>{row.status}</td><td>{row.characterCount.toLocaleString()}</td><td>{row.userId ?? 'Unattributed'}</td><td><button onClick={() => setSelected(row)}><Eye size={14} />Details</button></td></tr>)}</tbody></table></div><Pagination skip={skip} top={25} total={data.summary.requests} onSkip={setSkip} /></section>
      </>}
    </div>}
    <Modal open={selected != null} title="Content Safety analysis" onClose={() => setSelected(null)} icon={<ShieldCheck size={18} />}>
      {selected && <dl className="embedding-details">{Object.entries(selected).map(([key, value]) => <div key={key}><dt>{key.replace(/([a-z])([A-Z])/g, '$1 $2')}</dt><dd><span>{value == null ? 'Not available' : String(value)}</span>{typeof value === 'string' && <CopyButton value={value} />}</dd></div>)}</dl>}
    </Modal>
  </div>
}
