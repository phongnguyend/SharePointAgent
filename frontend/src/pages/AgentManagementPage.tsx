import { useId } from 'react'
import { Bot, BookOpen } from 'lucide-react'
import { useLocation, useNavigate } from 'react-router-dom'
import AgentsPage from './AgentsPage'
import AgentSkillsPage from './AgentSkillsPage'

export default function AgentManagementPage() {
  const id = useId()
  const navigate = useNavigate()
  const skills = useLocation().pathname === '/agents/skills'
  const tabs = [
    { key: 'agents', label: 'Agents', path: '/agents', Icon: Bot, selected: !skills },
    { key: 'skills', label: 'Global Skills', path: '/agents/skills', Icon: BookOpen, selected: skills },
  ]

  return <div className="stack">
    <div className="page-tabs" role="tablist" aria-label="Agent management" onKeyDown={event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
        return
      }
      event.preventDefault()
      const next = event.key === 'Home' ? tabs[0] : event.key === 'End' ? tabs[1] : tabs[skills ? 0 : 1]
      navigate(next.path)
      event.currentTarget.querySelector<HTMLButtonElement>(`[data-agent-tab="${next.key}"]`)?.focus()
    }}>
      {tabs.map(({ key, label, path, Icon, selected }) => <button key={key} type="button" role="tab"
        id={`${id}-${key}`} data-agent-tab={key} aria-selected={selected} aria-controls={`${id}-${key}-panel`}
        tabIndex={selected ? 0 : -1} onClick={() => navigate(path)}><Icon size={15} aria-hidden="true" />{label}</button>)}
    </div>
    {tabs.map(({ key, selected }) => <div key={key} role="tabpanel" id={`${id}-${key}-panel`}
      aria-labelledby={`${id}-${key}`} hidden={!selected}>
      {selected && (key === 'agents' ? <AgentsPage /> : <AgentSkillsPage />)}
    </div>)}
  </div>
}
