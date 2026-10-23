import { useId } from 'react'
import { useSearchParams } from 'react-router-dom'
import { BrainCircuit, Image, ListTree, FileText, ShieldCheck, Mic, Network, Globe } from 'lucide-react'
import HttpPlaygroundPage from './HttpPlaygroundPage'
import KnowledgeGraphPlaygroundsPage from './KnowledgeGraphPlaygroundsPage'
import DecisionMakingPlaygroundPage from './DecisionMakingPlaygroundPage'
import ImagePlaygroundsPage from './ImagePlaygroundsPage'
import DocumentPlaygroundsPage, { isDocumentPlaygroundProvider } from './DocumentPlaygroundsPage'
import WorkspacePlaygroundsPage from './WorkspacePlaygroundsPage'
import ContentSafetyPlaygroundPage from './ContentSafetyPlaygroundPage'
import VoicePlaygroundsPage from './VoicePlaygroundsPage'

const providers = [
  { key: 'decision-making', label: 'Decision Making', Icon: BrainCircuit },
  { key: 'knowledge-graph', label: 'Knowledge Graph', Icon: Network },
  { key: 'image', label: 'Image', Icon: Image },
  { key: 'content-safety', label: 'Content Safety', Icon: ShieldCheck },
  { key: 'voice', label: 'Voice', Icon: Mic },
  { key: 'document', label: 'Document', Icon: FileText },
  { key: 'workspace', label: 'Workspace', Icon: ListTree },
  { key: 'http', label: 'HTTP', Icon: Globe },
] as const

export default function PlaygroundsPage() {
  const [params, setParams] = useSearchParams()
  const id = useId()
  const requested = params.get('provider') === 'graph-query' ? 'knowledge-graph' : params.get('provider')
  const selected = requested === 'image-description' || requested === 'image-text' ? 'image'
    : isDocumentPlaygroundProvider(requested) ? 'document'
    : requested === 'dynamic-sessions' || requested === 'sandboxes' ? 'workspace'
    : providers.find(provider => provider.key === requested)?.key ?? 'decision-making'
  const select = (provider: string) => {
    const next = new URLSearchParams(params)
    const previous = params.get('provider')
    if (!params.has('decision') && (previous === 'jev' || previous === 'microsoft-decision' || previous === 'ollaya' || previous === 'winow-native' || previous === 'laya-native')) {
      next.set('decision', previous)
    }
    if (!params.has('image') && (previous === 'image-description' || previous === 'image-text')) {
      next.set('image', previous)
    }
    if (!params.has('document') && previous && isDocumentPlaygroundProvider(previous)) {
      next.set('document', previous)
    }
    if (!params.has('workspace') && (previous === 'dynamic-sessions' || previous === 'sandboxes')) {
      next.set('workspace', previous)
    }
    next.set('tab', 'playgrounds')
    next.set('provider', provider)
    setParams(next)
  }

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
      {key === 'knowledge-graph' ? <KnowledgeGraphPlaygroundsPage />
        : key === 'http' ? <HttpPlaygroundPage />
        : key === 'content-safety' ? <ContentSafetyPlaygroundPage active={selected === key} />
        : key === 'voice' ? <VoicePlaygroundsPage active={selected === key} />
        : key === 'workspace'
        ? <WorkspacePlaygroundsPage active={selected === key} />
        : key === 'image'
        ? <ImagePlaygroundsPage active={selected === key} />
        : key === 'decision-making'
          ? <DecisionMakingPlaygroundPage />
          : <DocumentPlaygroundsPage />}
    </div>)}
  </div>
}
