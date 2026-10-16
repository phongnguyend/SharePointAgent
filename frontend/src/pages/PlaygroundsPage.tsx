import { useId } from 'react'
import { useSearchParams } from 'react-router-dom'
import { BrainCircuit, GitBranch, Image, ScanText, FileDown, ListTree, FileType, FileText, Sheet, Presentation } from 'lucide-react'
import DecisionPlaygroundPage from './DecisionPlaygroundPage'
import ImagePlaygroundPage from './ImagePlaygroundPage'
import DocumentPlaygroundPage from './DocumentPlaygroundPage'
import WorkspacePlaygroundPage from './WorkspacePlaygroundPage'

const providers = [
  { key: 'jev', label: 'Jev', Icon: BrainCircuit },
  { key: 'microsoft-decision', label: 'Microsoft Decision', Icon: GitBranch },
  { key: 'image-description', label: 'Image Description', Icon: Image },
  { key: 'image-text', label: 'Image Text Extraction', Icon: ScanText },
  { key: 'markitdown', label: 'MarkItDown', Icon: FileDown },
  { key: 'pageindex', label: 'PageIndex', Icon: ListTree },
  { key: 'pdf', label: 'PDF', Icon: FileType },
  { key: 'docx', label: 'DOCX', Icon: FileText },
  { key: 'xlsx', label: 'XLSX', Icon: Sheet },
  { key: 'pptx', label: 'PPTX', Icon: Presentation },
  { key: 'dynamic-sessions', label: 'ACA Dynamic Sessions', Icon: ListTree },
  { key: 'sandboxes', label: 'ACA Sandboxes', Icon: FileType },
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
      {providers.map(({ key, label, Icon }) => <button key={key} type="button" role="tab" data-provider={key}
        id={`${id}-${key}`} aria-controls={`${id}-${key}-panel`} aria-selected={selected === key}
        tabIndex={selected === key ? 0 : -1} onClick={() => select(key)}><Icon size={15} aria-hidden="true" />{label}</button>)}
    </div>
    {/* Keep all playgrounds mounted so switching preserves inputs, results, and pending requests. */}
    {providers.map(({ key }) => <div key={key} role="tabpanel" id={`${id}-${key}-panel`}
      aria-labelledby={`${id}-${key}`} hidden={selected !== key}>
      {key === 'dynamic-sessions' || key === 'sandboxes'
        ? <WorkspacePlaygroundPage provider={key} active={selected === key} />
        : key === 'image-description' || key === 'image-text'
        ? <ImagePlaygroundPage operation={key === 'image-description' ? 'describe' : 'extract'} />
        : key !== 'jev' && key !== 'microsoft-decision'
          ? <DocumentPlaygroundPage provider={key} />
          : <DecisionPlaygroundPage provider={key} />}
    </div>)}
  </div>
}
