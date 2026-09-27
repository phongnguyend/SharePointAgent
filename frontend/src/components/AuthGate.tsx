import { useEffect, useState, type ReactNode } from 'react'
import { initializeAuth, signedInAccount, signIn, signOut } from '../auth'
import { ArrowRight, FileSearch, Files, LoaderCircle, LogOut, MessageSquareText, ShieldCheck, Sparkles } from 'lucide-react'
import { Modal } from './ui'
import { AppUserContext, useAppUser } from './AppUserContext'
import { getCurrentUser } from '../api/client'
import type { AppUser } from '../api/types'

export function AuthGate({ children }: { children: ReactNode }) {
  const [ready, setReady] = useState(false)
  const [authenticated, setAuthenticated] = useState(false)
  const [appUser, setAppUser] = useState<AppUser | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  useEffect(() => {
    let active = true
    const expired = () => { setAuthenticated(false); setAppUser(null); setError('Please sign in again to continue.') }
    const refresh = async () => {
      if (!signedInAccount()) return
      try {
        const user = await getCurrentUser()
        if (active) { setAppUser(user); setAuthenticated(true); setError(null) }
      } catch (cause) {
        if (active) { setAuthenticated(false); setAppUser(null); setError(cause instanceof Error ? cause.message : 'Could not load your application account.') }
      }
    }
    window.addEventListener('auth-required', expired)
    window.addEventListener('app-profile-refresh', refresh)
    window.addEventListener('focus', refresh)
    initializeAuth().then(async () => {
      await refresh()
      if (active) setReady(true)
    }).catch((cause: unknown) => {
      if (active) { setError(cause instanceof Error ? cause.message : 'Sign-in initialization failed.'); setReady(true) }
    })
    return () => { active = false; window.removeEventListener('auth-required', expired); window.removeEventListener('app-profile-refresh', refresh); window.removeEventListener('focus', refresh) }
  }, [])
  if (authenticated && appUser) return <AppUserContext.Provider value={appUser}>{children}</AppUserContext.Provider>
  return (
    <main className="auth-screen">
      <div className="auth-layout">
        <section className="auth-intro" aria-labelledby="auth-heading">
          <div className="auth-brand"><span className="auth-brand-mark"><Files size={23} aria-hidden="true" /></span>SharePoint Agent</div>
          <div className="auth-intro-content">
            <span className="auth-eyebrow"><Sparkles size={14} aria-hidden="true" />YOUR KNOWLEDGE, CONNECTED</span>
            <h1 id="auth-heading">From documents<br />to <em>answers.</em></h1>
            <p>Find what matters in SharePoint. Explore your files and ask questions, all in one place.</p>
            <div className="auth-features">
              <span><FileSearch size={17} aria-hidden="true" />Search your knowledge</span>
              <span><MessageSquareText size={17} aria-hidden="true" />Chat with your documents</span>
            </div>
          </div>
          <span className="auth-intro-footer">Built for your organization.</span>
        </section>
        <section className="auth-card" aria-labelledby="auth-welcome">
          <div className="auth-welcome-icon"><ShieldCheck size={27} aria-hidden="true" /></div>
          <h2 id="auth-welcome">Welcome back</h2>
          <p className="auth-description">Use your organization’s Microsoft Entra ID account to open your workspace.</p>
          {error && <p className="auth-error" role="alert">{error}</p>}
          <button className="auth-signin" disabled={!ready || busy} onClick={() => {
            setBusy(true)
            void signIn().catch((cause: unknown) => { setError(cause instanceof Error ? cause.message : 'Sign-in failed.'); setBusy(false) })
          }}>
            {!ready || busy ? <LoaderCircle className="auth-spinner" size={18} aria-hidden="true" /> : <span className="microsoft-mark" aria-hidden="true"><i /><i /><i /><i /></span>}
            <span>{!ready ? 'Getting ready…' : busy ? 'Opening sign-in…' : 'Sign in with your organization'}</span>
            {ready && !busy && <ArrowRight size={17} aria-hidden="true" />}
          </button>
          {error && <button className="ghost auth-retry" onClick={() => window.location.reload()}>Try again</button>}
          <div className="auth-trust"><ShieldCheck size={14} aria-hidden="true" /><span>Sign-in powered by Microsoft Entra ID</span></div>
          <p className="auth-help">Need access? Contact your organization’s administrator.</p>
        </section>
      </div>
    </main>
  )
}

export function AccountMenu() {
  const appUser = useAppUser()
  const [error, setError] = useState<string | null>(null)
  const [confirmOpen, setConfirmOpen] = useState(false)
  const [signingOut, setSigningOut] = useState(false)
  const account = signedInAccount()
  const close = () => { if (!signingOut) setConfirmOpen(false) }
  const confirmSignOut = async () => {
    setSigningOut(true)
    setError(null)
    try {
      await signOut()
    } catch {
      setError('Sign-out failed. Please try again.')
      setSigningOut(false)
    }
  }
  return <div className="account-menu">
    <span title={`${account?.username} · ${appUser.roles.join(', ')}`}>{appUser.displayName || account?.name || account?.username}</span>
    <button onClick={() => { setError(null); setConfirmOpen(true) }}><LogOut size={14} aria-hidden="true" />Sign out</button>
    <Modal
      open={confirmOpen}
      title="Sign out?"
      icon={<LogOut size={18} aria-hidden="true" />}
      onClose={close}
      footer={<>
        <button autoFocus disabled={signingOut} onClick={close}>Cancel</button>
        <button disabled={signingOut} onClick={() => void confirmSignOut()}>
          <LogOut size={14} aria-hidden="true" />{signingOut ? 'Signing out…' : 'Sign out'}
        </button>
      </>}
    >
      <p>Are you sure you want to sign out of SharePoint Agent?</p>
      {error && <p role="alert">{error}</p>}
    </Modal>
  </div>
}
