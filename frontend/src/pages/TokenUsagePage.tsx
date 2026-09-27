import { ChartNoAxesCombined, Cpu, Layers } from 'lucide-react'
import { useSearchParams } from 'react-router-dom'
import EmbeddingUsageReport from '../components/EmbeddingUsageReport'
import ChatTokenUsageReport from '../components/ChatTokenUsageReport'

export default function TokenUsagePage() {
  const [params, setParams] = useSearchParams()
  const active = params.get('tab') === 'embeddings' ? 'embeddings' : 'tokens'
  const select = (tab: string) => setParams({ tab })
  return <div className="stack">
    <div className="page-head"><div><h1><ChartNoAxesCombined size={20} />Token usage</h1><p>Explore chat and embedding consumption across models and users.</p></div></div>
    <div className="card card-head embedding-tabs" role="tablist" aria-label="Token usage reports">
      {(['tokens', 'embeddings'] as const).map(tab => <button key={tab} id={`usage-tab-${tab}`} role="tab" aria-selected={active === tab} aria-controls={`usage-panel-${tab}`} tabIndex={active === tab ? 0 : -1} className={active === tab ? 'primary' : 'ghost'} onClick={() => select(tab)} onKeyDown={event => {
        let next: string
        if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
          next = tab === 'tokens' ? 'embeddings' : 'tokens'
        } else if (event.key === 'Home') {
          next = 'tokens'
        } else if (event.key === 'End') {
          next = 'embeddings'
        } else {
          return
        }
        event.preventDefault()
        select(next)
        document.getElementById(`usage-tab-${next}`)?.focus()
      }}>{tab === 'tokens' ? <Cpu size={15} /> : <Layers size={15} />}{tab === 'tokens' ? 'Token Usage' : 'Embedding Usage'}</button>)}
    </div>
    <div role="tabpanel" id={`usage-panel-${active}`} aria-labelledby={`usage-tab-${active}`} tabIndex={0}>
      {active === 'tokens' ? <ChatTokenUsageReport /> : <EmbeddingUsageReport />}
    </div>
  </div>
}
