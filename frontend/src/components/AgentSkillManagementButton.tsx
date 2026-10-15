import { useState } from 'react'
import { BookOpen } from 'lucide-react'
import type { AgentSkillScope } from '../api/agentSkillClient'
import AgentSkillManager from './AgentSkillManager'
import { Modal } from './ui'

export default function AgentSkillManagementButton({ scope, readOnly, onChanged, disabled }: {
  scope: AgentSkillScope; readOnly?: boolean; onChanged?: (removedName?: string) => void; disabled?: boolean
}) {
  const [open, setOpen] = useState(false)
  const [busy, setBusy] = useState(false)
  return <>
    <div className="row"><button disabled={disabled} onClick={() => setOpen(true)}><BookOpen size={14} />{readOnly ? 'Browse skill files' : 'Manage skill files'}</button>
      {!scope.agentId ? <span className="hint">Save this agent before uploading its own skills.</span> : null}</div>
    {open ? <Modal open title={readOnly ? 'Skill files' : 'Manage agent skills'} className="agent-editor-modal" onClose={() => {
      if (!busy) {
        setOpen(false)
      }
    }} footer={<button disabled={busy} onClick={() => setOpen(false)}>Close</button>}>
      <AgentSkillManager scope={scope} readOnly={readOnly || !scope.agentId} onChanged={onChanged} onBusyChange={setBusy} />
    </Modal> : null}
  </>
}
