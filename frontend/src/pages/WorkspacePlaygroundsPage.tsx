import { useId } from 'react'
import { useSearchParams } from 'react-router-dom'
import { ListTree, FileType } from 'lucide-react'
import WorkspacePlaygroundPage from './WorkspacePlaygroundPage'

const tabs = [
  { key: 'dynamic-sessions', label: 'ACA Dynamic Sessions', Icon: ListTree },
  { key: 'sandboxes', label: 'ACA Sandboxes', Icon: FileType },
] as const

export default function WorkspacePlaygroundsPage({ active }: { active: boolean }) {
  const [params, setParams] = useSearchParams()
  const id = useId()
  const selected = tabs.find(tab => tab.key === (params.get('workspace') ?? params.get('provider')))?.key ?? 'dynamic-sessions'
  const select = (key: string) => {
    const next = new URLSearchParams(params)
    next.set('tab', 'playgrounds')
    next.set('provider', 'workspace')
    next.set('workspace', key)
    setParams(next)
  }

  return <div className="stack">
    <div className="page-tabs page-tabs-secondary" role="tablist" aria-label="Workspace playgrounds" onKeyDown={event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
        return
      }
      event.preventDefault()
      const current = tabs.findIndex(tab => tab.key === selected)
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1
        : (current + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length
      select(tabs[next].key)
      event.currentTarget.querySelector<HTMLButtonElement>(`[data-workspace-tab="${tabs[next].key}"]`)?.focus()
    }}>
      {tabs.map(({ key, label, Icon }) => <button key={key} type="button" role="tab" data-workspace-tab={key}
        id={`${id}-${key}`} aria-controls={`${id}-${key}-panel`} aria-selected={selected === key}
        tabIndex={selected === key ? 0 : -1} onClick={() => select(key)}><Icon size={15} aria-hidden="true" />{label}</button>)}
    </div>
    {tabs.map(({ key }) => <div key={key} role="tabpanel" id={`${id}-${key}-panel`}
      aria-labelledby={`${id}-${key}`} hidden={selected !== key}>
      <WorkspacePlaygroundPage provider={key} active={active && selected === key} />
    </div>)}
  </div>
}
