import { useId } from 'react'
import { useSearchParams } from 'react-router-dom'
import { BrainCircuit, GitBranch, Server } from 'lucide-react'
import DecisionPlaygroundPage from './DecisionPlaygroundPage'

const providers = [
  { key: 'jev', label: 'Jev', Icon: BrainCircuit },
  { key: 'microsoft-decision', label: 'Microsoft Decision', Icon: GitBranch },
  { key: 'ollaya', label: 'Ollaya', Icon: Server },
  { key: 'winnow-native', label: 'Winnow Native', Icon: Server },
  { key: 'laya-native', label: 'Laya Native', Icon: Server },
] as const

export default function DecisionMakingPlaygroundPage() {
  const [params, setParams] = useSearchParams()
  const id = useId()
  const selected = providers.find(provider => provider.key === (params.get('decision') ?? params.get('provider')))?.key ?? 'jev'
  const select = (provider: string) => {
    const next = new URLSearchParams(params)
    next.set('tab', 'playgrounds')
    next.set('provider', 'decision-making')
    next.set('decision', provider)
    setParams(next)
  }

  return <div className="stack">
    <div className="page-tabs page-tabs-secondary" role="tablist" aria-label="Decision Making playgrounds" onKeyDown={event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
        return
      }
      event.preventDefault()
      const current = providers.findIndex(provider => provider.key === selected)
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? providers.length - 1
        : (current + (event.key === 'ArrowRight' ? 1 : -1) + providers.length) % providers.length
      select(providers[next].key)
      event.currentTarget.querySelector<HTMLButtonElement>(`[data-decision-provider="${providers[next].key}"]`)?.focus()
    }}>
      {providers.map(({ key, label, Icon }) => <button key={key} type="button" role="tab" data-decision-provider={key}
        id={`${id}-${key}`} aria-controls={`${id}-${key}-panel`} aria-selected={selected === key}
        tabIndex={selected === key ? 0 : -1} onClick={() => select(key)}><Icon size={15} aria-hidden="true" />{label}</button>)}
    </div>
    {providers.map(({ key }) => <div key={key} role="tabpanel" id={`${id}-${key}-panel`}
      aria-labelledby={`${id}-${key}`} hidden={selected !== key}>
      <DecisionPlaygroundPage provider={key} />
    </div>)}
  </div>
}
