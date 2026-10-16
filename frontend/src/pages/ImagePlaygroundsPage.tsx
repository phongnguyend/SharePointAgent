import { useId } from 'react'
import { useSearchParams } from 'react-router-dom'
import { Image, ScanText } from 'lucide-react'
import ImagePlaygroundPage from './ImagePlaygroundPage'

const tabs = [
  { key: 'image-description', label: 'Image Description', operation: 'describe', Icon: Image },
  { key: 'image-text', label: 'Image Text Extraction', operation: 'extract', Icon: ScanText },
] as const

export default function ImagePlaygroundsPage({ active }: { active: boolean }) {
  const [params, setParams] = useSearchParams()
  const id = useId()
  const selected = tabs.find(tab => tab.key === (params.get('image') ?? params.get('provider')))?.key ?? 'image-description'
  const select = (key: string) => {
    const next = new URLSearchParams(params)
    next.set('tab', 'playgrounds')
    next.set('provider', 'image')
    next.set('image', key)
    setParams(next)
  }

  return <div className="stack">
    <div className="page-tabs page-tabs-secondary" role="tablist" aria-label="Image playgrounds" onKeyDown={event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
        return
      }
      event.preventDefault()
      const current = tabs.findIndex(tab => tab.key === selected)
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1
        : (current + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length
      select(tabs[next].key)
      event.currentTarget.querySelector<HTMLButtonElement>(`[data-image-tab="${tabs[next].key}"]`)?.focus()
    }}>
      {tabs.map(({ key, label, Icon }) => <button key={key} type="button" role="tab" data-image-tab={key}
        id={`${id}-${key}`} aria-controls={`${id}-${key}-panel`} aria-selected={selected === key}
        tabIndex={selected === key ? 0 : -1} onClick={() => select(key)}><Icon size={15} aria-hidden="true" />{label}</button>)}
    </div>
    {tabs.map(({ key, operation }) => <div key={key} role="tabpanel" id={`${id}-${key}-panel`}
      aria-labelledby={`${id}-${key}`} hidden={selected !== key}>
      <ImagePlaygroundPage operation={operation} active={active && selected === key} />
    </div>)}
  </div>
}
