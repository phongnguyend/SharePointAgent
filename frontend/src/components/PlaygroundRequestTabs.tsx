import { useCallback, useId, useRef, useState, type ReactNode } from 'react'
import { FileText, LoaderCircle, Maximize2, Minimize2, Plus, X } from 'lucide-react'
import { usePlaygroundMaximized } from './usePlaygroundMaximized'
import './PlaygroundRequestTabs.css'

export type PlaygroundRequestTab = {
  id: number
  active: boolean
  tabBar: ReactNode
  headerActions: ReactNode
  panelId: string
  tabId: string
  onSummary: (id: number, title: string, running: boolean) => void
}

export function PlaygroundRequestTabs({ label, active, children }: {
  label: string; active: boolean; children: (tab: PlaygroundRequestTab) => ReactNode
}) {
  const uid = useId()
  const container = useRef<HTMLDivElement>(null)
  const { fullscreen, toggleFullscreen } = usePlaygroundMaximized(active)
  const [tabs, setTabs] = useState([{ id: 1, number: 1, title: 'Request 1', running: false }])
  const [selected, setSelected] = useState(1)
  const sequence = useRef(1)
  const requestNumber = useRef(1)
  const list = useRef<HTMLDivElement>(null)
  const onSummary = useCallback((id: number, title: string, running: boolean) => {
    setTabs(current => current.map(tab => tab.id === id ? { ...tab, title: title || `Request ${tab.number}`, running } : tab))
  }, [])
  const focus = () => requestAnimationFrame(() => {
    const button = list.current?.querySelector<HTMLButtonElement>('[aria-selected="true"]')
    button?.focus()
    button?.scrollIntoView({ block: 'nearest', inline: 'nearest' })
  })
  const create = () => {
    const id = ++sequence.current
    const number = ++requestNumber.current
    return { id, number, title: `Request ${number}`, running: false }
  }
  const close = (id: number) => {
    const index = tabs.findIndex(tab => tab.id === id)
    const remaining = tabs.filter(tab => tab.id !== id)
    if (!remaining.length) {
      requestNumber.current = 0
      remaining.push(create())
    }
    setTabs(remaining)
    if (selected === id) {
      setSelected(remaining[Math.min(index, remaining.length - 1)].id)
    }
    focus()
  }
  const headerActions = <button type="button" className="ghost icon-only playground-fullscreen-toggle"
      aria-label={fullscreen ? 'Restore playground' : 'Maximize playground'} title={fullscreen ? 'Restore playground (Esc)' : 'Maximize playground'}
      aria-pressed={fullscreen} onClick={() => void toggleFullscreen()}>
      {fullscreen ? <Minimize2 size={17} /> : <Maximize2 size={17} />}
    </button>
  const bar = <div className="playground-request-tabbar">
    <div className="playground-request-tabs" ref={list} role="tablist" aria-label={`${label} requests`} onKeyDown={event => {
      if (!(event.target instanceof HTMLElement) || event.target.getAttribute('role') !== 'tab') {
        return
      }
      if (event.key === 'Delete') {
        event.preventDefault()
        close(selected)
        return
      }
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
        return
      }
      event.preventDefault()
      const current = tabs.findIndex(tab => tab.id === selected)
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1
        : (current + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length
      setSelected(tabs[next].id)
      focus()
    }}>
      {tabs.map(tab => <div key={tab.id} role="presentation" className="playground-request-tab" data-selected={tab.id === selected}>
        <button type="button" role="tab" id={`${uid}-tab-${tab.id}`} aria-controls={`${uid}-panel-${tab.id}`} aria-selected={tab.id === selected}
          tabIndex={tab.id === selected ? 0 : -1} title={tab.title} onClick={() => setSelected(tab.id)}>
          <FileText size={14} aria-hidden="true" /><span className="playground-request-title">{tab.title}</span>
          {tab.running && <LoaderCircle size={14} className="playground-request-loading" aria-label="Request running" />}
        </button>
        <button type="button" className="ghost icon-only" aria-label={`Close ${tab.title}`} title={tab.running ? 'Close and cancel request' : 'Close request'} onClick={() => close(tab.id)}><X size={13} /></button>
      </div>)}
    </div>
    <button type="button" className="ghost icon-only" aria-label="New request tab" title="New request tab" onClick={() => {
      const tab = create()
      setTabs(current => [...current, tab])
      setSelected(tab.id)
      focus()
    }}><Plus size={17} /></button>

  </div>
  return <div ref={container} className={`playground-request-container${fullscreen ? ' is-maximized' : ''}`}>{tabs.map(tab => <div key={tab.id} hidden={selected !== tab.id}>
    {children({ id: tab.id, active: active && selected === tab.id, tabBar: selected === tab.id ? bar : null, headerActions: selected === tab.id ? headerActions : null,
      panelId: `${uid}-panel-${tab.id}`, tabId: `${uid}-tab-${tab.id}`, onSummary })}
  </div>)}</div>
}
