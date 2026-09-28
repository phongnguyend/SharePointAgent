import { useState } from 'react'
import { Activity, ArrowDownLeft, ArrowUpRight, Cpu, Eye, Filter, RefreshCw, RotateCcw, Users } from 'lucide-react'
import { getChatTokenUsage } from '../api/client'
import type { ChatUsageFilter, ChatUsageRecord } from '../api/types'
import { CopyButton, Empty, ErrorBanner, LoadingBar, Modal, Pagination, StatTile } from './ui'
import { useAsync } from '../lib/useAsync'

const number = (value: number) => value.toLocaleString()
const dateKey = (value: number) => String(value).replace(/^(\d{4})(\d{2})(\d{2})$/, '$1-$2-$3')
function initialFilters(): ChatUsageFilter {
  const today = new Date().toISOString().slice(0, 10)
  return { from: `${today.slice(0, 7)}-01`, to: today, model: '', user: '', questionId: '', unknownModel: false }
}

export default function ChatTokenUsageReport() {
  const [filters, setFilters] = useState(initialFilters)
  const [draft, setDraft] = useState(initialFilters)
  const [skip, setSkip] = useState(0)
  const [tab, setTab] = useState<'turns' | 'daily' | 'models' | 'users'>('turns')
  const [selected, setSelected] = useState<ChatUsageRecord | null>(null)
  const report = useAsync(signal => getChatTokenUsage(filters, skip, signal), [filters, skip])
  const data = report.data
  const apply = (next: ChatUsageFilter) => {
    setFilters(next)
    setDraft(next)
    setSkip(0)
  }
  const days: { day: string; total: number; input: number; output: number; turns: number }[] = []
  if (data) {
    const values = new Map(data.daily.map(row => [dateKey(row.day), row]))
    const end = new Date(`${data.to}T00:00:00Z`)
    for (const day = new Date(`${data.from}T00:00:00Z`); day <= end; day.setUTCDate(day.getUTCDate() + 1)) {
      const key = day.toISOString().slice(0, 10)
      const row = values.get(key)
      days.push({ day: key, total: row?.totalTokens ?? 0, input: row?.inputTokens ?? 0, output: row?.outputTokens ?? 0, turns: row?.turns ?? 0 })
    }
  }
  const peak = Math.max(1, ...days.map(row => row.total))
  const modelPeak = Math.max(1, ...(data?.models.map(row => row.totalTokens) ?? []))
  const userPeak = Math.max(1, ...(data?.users.map(row => row.totalTokens) ?? []))
  return <div className="stack embedding-page">
    <div className="row spread"><span className="muted">Chat model tokens, including tool-call rounds. Embeddings are excluded.</span><button onClick={report.reload} disabled={report.loading}><RefreshCw size={14} />Refresh</button></div>
    <form className="card embedding-filters chat-token-filters" onSubmit={event => {
      event.preventDefault()
      apply({ ...draft })
    }}>
      <label>From (UTC)<input type="date" required max={draft.to} value={draft.from} onChange={event => setDraft({ ...draft, from: event.target.value })} /></label>
      <label>Through (UTC)<input type="date" required min={draft.from} value={draft.to} onChange={event => setDraft({ ...draft, to: event.target.value })} /></label>
      <label>Chat model<select value={draft.unknownModel ? '__unknown__' : draft.model} onChange={event => setDraft({ ...draft, unknownModel: event.target.value === '__unknown__', model: event.target.value === '__unknown__' ? '' : event.target.value })}><option value="">All models</option><option value="__unknown__">Unknown / historical</option>{[...new Set([...(data?.modelOptions.filter((model): model is string => model != null) ?? []), ...(draft.model ? [draft.model] : [])])].map(model => <option key={model}>{model}</option>)}</select></label>
      <label>User<input type="text" maxLength={200} placeholder="Name, email, or user ID" value={draft.user} onChange={event => setDraft({ ...draft, user: event.target.value })} /></label>
      <label>Question ID<input type="text" placeholder="Exact question GUID" pattern="[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}" value={draft.questionId} onChange={event => setDraft({ ...draft, questionId: event.target.value })} /></label>
      <div className="row embedding-filter-actions"><button type="submit" disabled={report.loading}><Filter size={14} />Apply filters</button><button type="button" onClick={() => apply(initialFilters())}><RotateCcw size={14} />Reset</button></div>
    </form>
    <LoadingBar active={report.loading} />
    {report.error && <ErrorBanner message={report.error} onRetry={report.reload} />}
    {data && !report.error && <div className="stack" aria-busy={report.loading} style={{ opacity: report.loading ? 0.55 : 1, pointerEvents: report.loading ? 'none' : undefined }}>
      <div className="tiles token-report-tiles">
        <StatTile label="Total chat tokens" value={number(data.summary.totalTokens)} icon={<Cpu size={15} />} hint="Embeddings excluded" />
        <StatTile label="Input tokens" value={number(data.summary.inputTokens)} icon={<ArrowDownLeft size={15} />} hint="Prompt and tool context" />
        <StatTile label="Output tokens" value={number(data.summary.outputTokens)} icon={<ArrowUpRight size={15} />} hint="Model output across rounds" />
        <StatTile label="Recorded turns" value={number(data.summary.turns)} icon={<Activity size={15} />} hint={`${number(data.summary.turns ? Math.round(data.summary.totalTokens / data.summary.turns) : 0)} tokens / turn`} />
        <StatTile label="Active users" value={number(data.summary.users)} icon={<Users size={15} />} hint={`${number(data.models.length)} model groups`} />
      </div>
      <p className="embedding-period muted">{data.from} – {data.to} UTC · Assigned to the day each turn started, matching monthly quotas. {number(data.summary.unknownModelTurns)} turns have no recorded model. Totals include all matching records, not just this page.</p>
      {data.summary.turns === 0 ? <div className="card"><Empty title="No chat token usage in this range" detail="Clear the filters or choose a wider date range. Historical conversations are not backfilled into the usage ledger." /></div> : <>
        <section className="card"><div className="card-head"><h2>Daily chat consumption</h2><span className="hint">Select a day to filter · Input and output shown in the tooltip</span></div><div className="card-body">
          <div className="embedding-chart" role="group" aria-label="Daily chat token usage">
            {days.map(day => <button key={day.day} className="embedding-bar" title={`${day.day}: ${number(day.total)} total · ${number(day.input)} input · ${number(day.output)} output · ${day.turns} turns`} aria-label={`${day.day}: ${number(day.total)} tokens. Filter to day.`} onClick={() => apply({ ...filters, from: day.day, to: day.day })}><span style={{ height: `${Math.max(2, day.total / peak * 100)}%`, opacity: day.total ? 1 : 0.2 }} /></button>)}
          </div><div className="row spread muted"><span>{data.from}</span><span>Exact values in Daily totals</span><span>{data.to}</span></div>
        </div></section>
        <div className="embedding-panels">
          <section className="card"><div className="card-head"><h2>By chat model</h2><span className="hint">Select to filter</span></div><div className="embedding-breakdown">{data.models.map(row => <button className="embedding-group" key={row.modelId ?? '__unknown__'} onClick={() => apply({ ...filters, model: row.modelId ?? '', unknownModel: row.modelId == null })}><span className="embedding-group-heading"><strong>{row.modelId ?? 'Unknown / historical'}</strong><strong>{number(row.totalTokens)}</strong></span><span className="embedding-meter"><span style={{ width: `${row.totalTokens / modelPeak * 100}%` }} /></span><span className="embedding-group-heading muted"><span>{number(row.turns)} turns</span><span>{number(row.inputTokens)} in / {number(row.outputTokens)} out</span></span></button>)}</div></section>
          <section className="card"><div className="card-head"><h2>Top users</h2><span className="hint">Top 100 by tokens · Select to filter</span></div><div className="embedding-breakdown">{data.users.map(row => <button className="embedding-group" key={row.userId} onClick={() => apply({ ...filters, user: row.userId })}><span className="embedding-group-heading"><strong>{row.name}</strong><strong>{number(row.totalTokens)}</strong></span><span className="embedding-meter"><span style={{ width: `${row.totalTokens / userPeak * 100}%` }} /></span><span className="embedding-group-heading muted"><span>{number(row.turns)} turns</span><span>{number(row.inputTokens)} in / {number(row.outputTokens)} out</span></span></button>)}</div></section>
        </div>
        <section className="card">
          <div className="card-head embedding-tabs" role="tablist" aria-label="Chat usage breakdown">{(['turns', 'daily', 'models', 'users'] as const).map((value, index, tabs) => <button key={value} id={`chat-report-${value}`} role="tab" aria-selected={tab === value} aria-controls="chat-report-panel" tabIndex={tab === value ? 0 : -1} className={tab === value ? 'primary' : 'ghost'} onClick={() => setTab(value)} onKeyDown={event => {
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
            document.getElementById(`chat-report-${tabs[next]}`)?.focus()
          }}>{({ turns: 'Turn log', daily: 'Daily totals', models: 'Models', users: 'Users' })[value]}</button>)}</div>
          <div role="tabpanel" id="chat-report-panel" aria-labelledby={`chat-report-${tab}`} tabIndex={0}><div className="table-scroll">
            {tab === 'turns' ? <table><thead><tr><th>Usage day (UTC)</th><th>User</th><th>Model</th><th>Input</th><th>Output</th><th>Total</th><th /></tr></thead><tbody>{data.items.map(({ usage, userName }) => <tr key={usage.questionId}><td className="nowrap">{dateKey(usage.day)}</td><td>{userName}</td><td>{usage.modelId ?? 'Unknown / historical'}</td><td>{number(usage.inputTokens)}</td><td>{number(usage.outputTokens)}</td><td><strong>{number(usage.totalTokens)}</strong></td><td><button onClick={() => setSelected(usage)}><Eye size={14} />Details</button></td></tr>)}</tbody></table> : <table><thead><tr><th>{tab === 'daily' ? 'Day (UTC)' : tab === 'models' ? 'Model' : 'User (top 100)'}</th><th>Turns</th><th>Input</th><th>Output</th><th>Total</th><th>Tokens / turn</th></tr></thead><tbody>{(tab === 'daily' ? data.daily.map(row => ({ ...row, key: String(row.day), name: dateKey(row.day) })) : tab === 'models' ? data.models.map(row => ({ ...row, key: row.modelId ?? '__unknown__', name: row.modelId ?? 'Unknown / historical' })) : data.users.map(row => ({ ...row, key: row.userId }))).map(row => <tr key={row.key}><td>{row.name}</td><td>{number(row.turns)}</td><td>{number(row.inputTokens)}</td><td>{number(row.outputTokens)}</td><td>{number(row.totalTokens)}</td><td>{number(Math.round(row.totalTokens / row.turns))}</td></tr>)}</tbody></table>}
          </div>{tab === 'turns' && <Pagination skip={skip} top={25} total={data.summary.turns} onSkip={setSkip} />}</div>
        </section>
      </>}
    </div>}
    <Modal open={selected !== null} title="Chat token usage details" icon={<Cpu size={18} />} onClose={() => setSelected(null)}>
      {selected && <dl className="embedding-details">{Object.entries(selected).map(([key, value]) => <div key={key}><dt>{key.replace(/([a-z])([A-Z])/g, '$1 $2').replace(/^./, first => first.toUpperCase())}</dt><dd><span>{value == null ? 'Not recorded' : String(value)}</span>{typeof value === 'string' && <CopyButton value={value} />}</dd></div>)}</dl>}
    </Modal>
  </div>
}
