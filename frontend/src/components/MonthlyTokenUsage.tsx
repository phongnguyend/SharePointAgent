import { useId, useState } from 'react'
import { createPortal } from 'react-dom'
import { Cpu, CalendarDays, X } from 'lucide-react'
import type { AppUser } from '../api/types'
import { Modal } from './ui'

const usageTabs = [
  { value: 'day', label: 'Day' },
  { value: 'model', label: 'Model ID' },
  { value: 'day-model', label: 'Day and model ID' },
] as const

export function MonthlyTokenUsage({ user, showDetails = false }: { user: AppUser; showDetails?: boolean }) {
  const [dailyOpen, setDailyOpen] = useState(false)
  const tabId = useId()
  const [groupBy, setGroupBy] = useState<'day' | 'model' | 'day-model'>('day')
  const used = user.monthlyTokensUsed
  const limit = user.monthlyTokenLimit
  const daily = user.dailyTokenUsage ?? []
  const dailyModels = user.dailyModelTokenUsage ?? []
  const models = new Map<string | null, { modelId: string | null; inputTokens: number; outputTokens: number; totalTokens: number }>()
  for (const row of dailyModels) {
    const total = models.get(row.modelId) ?? { modelId: row.modelId, inputTokens: 0, outputTokens: 0, totalTokens: 0 }
    total.inputTokens += row.inputTokens
    total.outputTokens += row.outputTokens
    total.totalTokens += row.totalTokens
    models.set(row.modelId, total)
  }
  const rows = groupBy === 'day'
    ? daily.map(row => ({ ...row, modelId: null as string | null }))
    : groupBy === 'day-model' ? dailyModels
    : [...models.values()].sort((a, b) => (a.modelId ?? '').localeCompare(b.modelId ?? '')).map(row => ({ ...row, day: 0 }))
  const today = Number(new Date().toISOString().slice(0, 10).replaceAll('-', ''))
  return <div className="attachment-storage-usage">
    <div className="token-usage-total">
    <div className="token-usage-heading">
    <span className="row"><Cpu size={14} aria-hidden="true" />{used.toLocaleString()} tokens / {limit === null ? 'Unlimited' : limit.toLocaleString()}</span>
    {showDetails && <button type="button" className="daily-token-usage-button" onClick={() => setDailyOpen(true)}><CalendarDays size={14} />Daily usage this month</button>}
    </div>
    {limit !== null && <progress aria-label="Monthly chat tokens used" max={limit || 1} value={limit === 0 ? 1 : Math.min(used, limit)} />}
    {limit !== null && used >= limit && <span className="badge warning">{limit === 0 ? 'Chat disabled' : 'Monthly limit reached'}</span>}
    </div>
    {showDetails && <>
      {user.tokenUsageResetsAtUtc && <span className="hint">Resets {new Date(user.tokenUsageResetsAtUtc).toISOString().slice(0, 10)} at 00:00 UTC</span>}
      <span className="hint">Today (UTC): {(daily.find(row => row.day === today)?.totalTokens ?? 0).toLocaleString()} tokens</span>
    </>}
    {createPortal(<Modal open={dailyOpen} title="Daily usage this month" icon={<CalendarDays size={18} />} onClose={() => setDailyOpen(false)} footer={<button type="button" onClick={() => setDailyOpen(false)}><X size={14} />Close</button>}>
      <p><strong>{user.displayName}</strong> · {user.email}</p>
      <div className="attachment-tabs" role="tablist" aria-label="Group token usage by" onKeyDown={event => {
        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return
        event.preventDefault()
        const current = usageTabs.findIndex(tab => tab.value === groupBy)
        const index = event.key === 'Home' ? 0 : event.key === 'End' ? usageTabs.length - 1
          : (current + (event.key === 'ArrowRight' ? 1 : -1) + usageTabs.length) % usageTabs.length
        const next = usageTabs[index].value
        setGroupBy(next)
        event.currentTarget.querySelector<HTMLButtonElement>(`[data-tab="${next}"]`)?.focus()
      }}>
        {usageTabs.map(tab => <button key={tab.value} type="button" role="tab" data-tab={tab.value}
          id={`${tabId}-${tab.value}`} aria-controls={`${tabId}-panel`} aria-selected={groupBy === tab.value}
          tabIndex={groupBy === tab.value ? 0 : -1} onClick={() => setGroupBy(tab.value)}>{tab.label}</button>)}
      </div>
      <div role="tabpanel" id={`${tabId}-panel`} aria-labelledby={`${tabId}-${groupBy}`} tabIndex={0}>
      {rows.length === 0 ? <p>No token usage recorded this month.</p> : <div className="table-scroll"><table><thead><tr>{groupBy !== 'model' && <th>Date (UTC)</th>}{groupBy !== 'day' && <th>Model ID</th>}<th>Input</th><th>Output</th><th>Total</th></tr></thead>
        <tbody>{rows.map(row => <tr key={JSON.stringify([row.day, row.modelId])}>{groupBy !== 'model' && <td>{String(row.day).replace(/(\d{4})(\d{2})(\d{2})/, '$1-$2-$3')}</td>}{groupBy !== 'day' && <td>{row.modelId ?? 'Unknown (not recorded)'}</td>}<td>{row.inputTokens.toLocaleString()}</td><td>{row.outputTokens.toLocaleString()}</td><td>{row.totalTokens.toLocaleString()}</td></tr>)}</tbody>
      </table></div>}
      </div>
    </Modal>, document.body)}
  </div>
}
