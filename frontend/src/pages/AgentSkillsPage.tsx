import AgentSkillManager from '../components/AgentSkillManager'
import { canManageAdministration, useAppUser } from '../components/AppUserContext'

export default function AgentSkillsPage() {
  const readOnly = !canManageAdministration(useAppUser())
  return <div className="stack">
    <AgentSkillManager readOnly={readOnly} />
  </div>
}
