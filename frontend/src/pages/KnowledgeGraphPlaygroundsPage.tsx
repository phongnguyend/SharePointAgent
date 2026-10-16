import { useId, useState } from 'react'
import { Network, ScanSearch } from 'lucide-react'
import GraphQueryPlaygroundPage from './GraphQueryPlaygroundPage'
import GraphExtractionPlaygroundPage from './GraphExtractionPlaygroundPage'

export default function KnowledgeGraphPlaygroundsPage() {
  const [extraction, setExtraction] = useState(true)
  const id = useId()
  const tabs = [
    { key: 'extraction', label: 'Entity and Relationship Extraction', Icon: Network, selected: extraction },
    { key: 'query', label: 'Query Plan', Icon: ScanSearch, selected: !extraction },
  ]
  return <div className="stack">
    <div className="page-tabs page-tabs-secondary" role="tablist" aria-label="Knowledge Graph playgrounds" onKeyDown={event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
        return
      }
      event.preventDefault()
      const next = event.key === 'Home' ? true : event.key === 'End' ? false : !extraction
      setExtraction(next)
      event.currentTarget.querySelector<HTMLButtonElement>(`[data-graph-tab="${next ? 'extraction' : 'query'}"]`)?.focus()
    }}>
      {tabs.map(({ key, label, Icon, selected }) => <button key={key} type="button" role="tab" data-graph-tab={key}
        id={`${id}-${key}`} aria-controls={`${id}-${key}-panel`} aria-selected={selected}
        tabIndex={selected ? 0 : -1} onClick={() => setExtraction(key === 'extraction')}><Icon size={15} aria-hidden="true" />{label}</button>)}
    </div>
    {tabs.map(({ key, selected }) => <div key={key} role="tabpanel" id={`${id}-${key}-panel`} aria-labelledby={`${id}-${key}`} hidden={!selected}>
      {key === 'extraction' ? <GraphExtractionPlaygroundPage /> : <GraphQueryPlaygroundPage />}
    </div>)}
  </div>
}
