import { useId } from 'react'
import { Navigate, useSearchParams } from 'react-router-dom'
import { FilePenLine, GitBranch, Webhook } from 'lucide-react'
import { canManageAdministration, canReadAdministration, useAppUser } from '../components/AppUserContext'
import DeltaStatePage from './DeltaStatePage'
import SubscriptionsPage from './SubscriptionsPage'
import DocumentSigningConfigurationPage from './DocumentSigningConfigurationPage'

export default function AdminPage() {
  const user = useAppUser()
  const [params, setParams] = useSearchParams()
  const id = useId()
  const tabs = [
    { key: 'delta', label: 'Delta state', Icon: GitBranch },
    { key: 'subscriptions', label: 'Subscriptions', Icon: Webhook },
    ...(canManageAdministration(user) ? [{ key: 'document-signing-configuration', label: 'Document signing', Icon: FilePenLine }] : []),
  ]
  const requested = params.get('tab') || 'delta'
  if (!canReadAdministration(user)) {
    return <Navigate to="/chat" replace />
  }
  if (requested === 'document-signing' && canManageAdministration(user)) {
    return <Navigate to="/admin?tab=document-signing-configuration" replace />
  }
  if (!tabs.some(tab => tab.key === requested)) {
    return <Navigate to="/admin?tab=delta" replace />
  }
  const select = (key: string) => setParams({ tab: key })

  return <div className="stack">
    <div className="attachment-tabs" role="tablist" aria-label="Administration" onKeyDown={event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) {
        return
      }
      event.preventDefault()
      const current = tabs.findIndex(tab => tab.key === requested)
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1
        : (current + (event.key === 'ArrowRight' ? 1 : -1) + tabs.length) % tabs.length
      select(tabs[next].key)
      event.currentTarget.querySelector<HTMLButtonElement>(`[data-tab="${tabs[next].key}"]`)?.focus()
    }}>
      {tabs.map(({ key, label, Icon }) => <button type="button" role="tab" key={key} data-tab={key}
        id={`${id}-${key}`} aria-controls={`${id}-${key}-panel`} aria-selected={requested === key}
        tabIndex={requested === key ? 0 : -1} onClick={() => select(key)}>
        <Icon size={15} aria-hidden="true" />{label}
      </button>)}
    </div>
    {tabs.map(({ key }) => <div key={key} role="tabpanel" id={`${id}-${key}-panel`}
      aria-labelledby={`${id}-${key}`} hidden={requested !== key}>
      {requested === key && (key === 'delta' ? <DeltaStatePage /> : key === 'subscriptions' ? <SubscriptionsPage /> : <DocumentSigningConfigurationPage />)}
    </div>)}
  </div>
}
