import { useId } from 'react'
import { useSearchParams } from 'react-router-dom'
import DecisionPlaygroundPage from './DecisionPlaygroundPage'
import ImagePlaygroundPage from './ImagePlaygroundPage'
import DocumentPlaygroundPage from './DocumentPlaygroundPage'

const providers = [
  { key: 'jev', label: 'Jev' },
  { key: 'microsoft-decision', label: 'Microsoft Decision' },
  { key: 'image-description', label: 'Image Description' },
  { key: 'image-text', label: 'Image Text Extraction' },
  { key: 'markitdown', label: 'MarkItDown' },
  { key: 'pageindex', label: 'PageIndex' },
] as const

export default function PlaygroundsPage() {
  const [params, setParams] = useSearchParams()
  const id = useId()
  const selected = providers.find(provider => provider.key === params.get('provider'))?.key ?? 'jev'
  const select = (provider: string) => setParams({ tab: 'playgrounds', provider })

  return <div className="stack">
    <div className="page-tabs page-tabs-secondary" role="tablist" aria-label="Playgrounds" onKeyDown={event => {
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
    {/* Keep all playgrounds mounted so switching preserves inputs, results, and pending requests. */}
    {providers.map(({ key }) => <div key={key} role="tabpanel" id={`${id}-${key}-panel`}
      aria-labelledby={`${id}-${key}`} hidden={selected !== key}>
      {key === 'image-description' || key === 'image-text'
        ? <ImagePlaygroundPage operation={key === 'image-description' ? 'describe' : 'extract'} />
        : key === 'markitdown' || key === 'pageindex'
          ? <DocumentPlaygroundPage provider={key} />
          : <DecisionPlaygroundPage provider={key} />}
    </div>)}
  </div>
}
