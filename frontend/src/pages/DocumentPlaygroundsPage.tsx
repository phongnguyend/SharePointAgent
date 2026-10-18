import { useId } from 'react'
import { useSearchParams } from 'react-router-dom'
import { FileDown, ListTree, FileType, FileText, Sheet, Presentation } from 'lucide-react'
import DocumentPlaygroundPage from './DocumentPlaygroundPage'

const tabs = [
  { key: 'markitdown', label: 'MarkItDown', Icon: FileDown },
  { key: 'pageindex', label: 'PageIndex', Icon: ListTree },
  { key: 'pdf', label: 'PDF', Icon: FileType },
  { key: 'docx', label: 'DOCX', Icon: FileText },
  { key: 'xlsx', label: 'XLSX', Icon: Sheet },
  { key: 'pptx', label: 'PPTX', Icon: Presentation },
] as const

export function isDocumentPlaygroundProvider(value: string | null) {
  return tabs.some(tab => tab.key === value)
}

export default function DocumentPlaygroundsPage() {
  const [params, setParams] = useSearchParams()
  const id = useId()
  const selected = tabs.find(tab => tab.key === (params.get('document') ?? params.get('provider')))?.key ?? 'markitdown'
  const select = (key: string) => {
    const next = new URLSearchParams(params)
    next.set('tab', 'playgrounds')
    next.set('provider', 'document')
    next.set('document', key)
    setParams(next)
  }

  return <div className="stack">
    <div className="page-tabs page-tabs-secondary" role="tablist" aria-label="Document playgrounds" onKeyDown={event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
        return
      }
      event.preventDefault()
      const current = tabs.findIndex(tab => tab.key === selected)
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1
        : (current + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length
      select(tabs[next].key)
      event.currentTarget.querySelector<HTMLButtonElement>(`[data-document-tab="${tabs[next].key}"]`)?.focus()
    }}>
      {tabs.map(({ key, label, Icon }) => <button key={key} type="button" role="tab" data-document-tab={key}
        id={`${id}-${key}`} aria-controls={`${id}-${key}-panel`} aria-selected={selected === key}
        tabIndex={selected === key ? 0 : -1} onClick={() => select(key)}><Icon size={15} aria-hidden="true" />{label}</button>)}
    </div>
    {tabs.map(({ key }) => <div key={key} role="tabpanel" id={`${id}-${key}-panel`}
      aria-labelledby={`${id}-${key}`} hidden={selected !== key}>
      <DocumentPlaygroundPage provider={key} active={selected === key} />
    </div>)}
  </div>
}
