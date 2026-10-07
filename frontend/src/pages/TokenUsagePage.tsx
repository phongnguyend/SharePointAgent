import { ChartNoAxesCombined, Cpu, Image, Layers, Mic } from 'lucide-react'
import ImageDescriptionUsageReport from '../components/ImageDescriptionUsageReport'
import { useSearchParams } from 'react-router-dom'
import EmbeddingUsageReport from '../components/EmbeddingUsageReport'
import ChatTokenUsageReport from '../components/ChatTokenUsageReport'
import ContentSafetyUsageReport from '../components/ContentSafetyUsageReport'
import VoiceUsageReport from '../components/VoiceUsageReport'

export default function TokenUsagePage() {
  const [params, setParams] = useSearchParams()
  const tabs = ['tokens', 'embeddings', 'safety', 'images', 'voice'] as const
  const active = tabs.find(tab => tab === params.get('tab')) ?? 'tokens'
  const select = (tab: string) => setParams({ tab })
  return <div className="stack">
    <div className="page-head"><div><h1><ChartNoAxesCombined size={20} />Token usage</h1><p>Explore chat, embedding, image description, voice dictation, and Content Safety usage across the application.</p></div></div>
    <div className="card card-head embedding-tabs" role="tablist" aria-label="Token usage reports">
      {tabs.map((tab, index) => <button key={tab} id={`usage-tab-${tab}`} role="tab" aria-selected={active === tab} aria-controls={`usage-panel-${tab}`} tabIndex={active === tab ? 0 : -1} className={active === tab ? 'primary' : 'ghost'} onClick={() => select(tab)} onKeyDown={event => {
        let next: string
        if (event.key === 'ArrowLeft' || event.key === 'ArrowRight') {
          next = tabs[(index + (event.key === 'ArrowRight' ? 1 : tabs.length - 1)) % tabs.length]
        } else if (event.key === 'Home') {
          next = 'tokens'
        } else if (event.key === 'End') {
          next = tabs[tabs.length - 1]
        } else {
          return
        }
        event.preventDefault()
        select(next)
        document.getElementById(`usage-tab-${next}`)?.focus()
      }}>{tab === 'tokens' ? <Cpu size={15} /> : tab === 'images' ? <Image size={15} /> : tab === 'voice' ? <Mic size={15} /> : <Layers size={15} />}{tab === 'tokens' ? 'Chat Usage' : tab === 'embeddings' ? 'Embedding Usage' : tab === 'images' ? 'Image Description' : tab === 'voice' ? 'Voice' : 'Content Safety'}</button>)}
    </div>
    <div role="tabpanel" id={`usage-panel-${active}`} aria-labelledby={`usage-tab-${active}`} tabIndex={0}>
      {active === 'tokens' ? <ChatTokenUsageReport /> : active === 'embeddings' ? <EmbeddingUsageReport /> : active === 'images' ? <ImageDescriptionUsageReport /> : active === 'voice' ? <VoiceUsageReport /> : <ContentSafetyUsageReport />}
    </div>
  </div>
}
