import { useId } from 'react'
import { useSearchParams } from 'react-router-dom'
import DecisionPlaygroundPage from './DecisionPlaygroundPage'

const providers = [
  { key: 'jev', label: 'Jev' },
  { key: 'microsoft-decision', label: 'Microsoft Decision' },
] as const

export default function PlaygroundsPage() {
  const [params, setParams] = useSearchParams()
  const id = useId()
  const selected = params.get('provider') === 'microsoft-decision' ? 'microsoft-decision' : 'jev'
  const select = (provider: string) => setParams({ tab: 'playgrounds', provider })

  return <div className="stack">
    <div className="attachment-tabs" role="tablist" aria-label="Decision playgrounds" onKeyDown={event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
        return
      }
      event.preventDefault()
      const current = providers.findIndex(provider => provider.key === selected)
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? providers.length - 1
        : (current + (event.key === 'ArrowRight' ? 1 : -1) + providers.length) % providers.length
      select(providers[next].key)
      event.currentTarget.querySelector<HTMLButtonElement>(`[data-provider="${providers[next].key}"]`)?.focus()
    }}>
      {providers.map(({ key, label }) => <button key={key} type="button" role="tab" data-provider={key}
        id={`${id}-${key}`} aria-controls={`${id}-${key}-panel`} aria-selected={selected === key}
        tabIndex={selected === key ? 0 : -1} onClick={() => select(key)}>{label}</button>)}
    </div>
    {/* Keep both providers mounted so switching preserves inputs, results, and pending requests. */}
    {providers.map(({ key }) => <div key={key} role="tabpanel" id={`${id}-${key}-panel`}
      aria-labelledby={`${id}-${key}`} hidden={selected !== key}>
      <DecisionPlaygroundPage provider={key} />
    </div>)}
  </div>
}
