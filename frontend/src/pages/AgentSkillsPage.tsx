import { ArrowLeft, BookOpen } from 'lucide-react'
import { Link } from 'react-router-dom'
import AgentSkillManager from '../components/AgentSkillManager'
import { canManageAdministration, useAppUser } from '../components/AppUserContext'

export default function AgentSkillsPage() {
  const readOnly = !canManageAdministration(useAppUser())
  return <div className="stack">
    <div className="page-head"><div><h1><BookOpen size={20} />Global skills</h1><p>Manage reusable skills and inspect their files.</p></div><Link className="button-link" to="/agents"><ArrowLeft size={14} />Agents</Link></div>
    <AgentSkillManager readOnly={readOnly} />
  </div>
}
