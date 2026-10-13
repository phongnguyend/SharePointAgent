import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react'
import { createPortal } from 'react-dom'
import { CheckCircle2, CircleAlert, Info, TriangleAlert, X } from 'lucide-react'

type ToastKind = 'success' | 'error' | 'warning' | 'info'
type ToastOptions = { duration?: number }
type ToastMessage = { id: number; kind: ToastKind; message: string; duration: number }
type ToastApi = Record<ToastKind, (message: string, options?: ToastOptions) => void>
const ToastContext = createContext<ToastApi | null>(null)

export function useToast() {
  const toast = useContext(ToastContext)
  if (!toast) {
    throw new Error('useToast must be used inside ToastProvider.')
  }
  return toast
}

export function ToastProvider({ children }: { children: ReactNode }) {
  const [messages, setMessages] = useState<ToastMessage[]>([])
  const [target, setTarget] = useState<HTMLElement>(() => document.body)
  const nextId = useRef(0)
  const dismiss = useCallback((id: number) => setMessages(current => current.filter(item => item.id !== id)), [])
  const show = useCallback((kind: ToastKind, message: string, options?: ToastOptions) => {
    if (!message.trim()) {
      return
    }
    const item = { id: ++nextId.current, kind, message, duration: options?.duration ?? (kind === 'error' ? 0 : 6000) }
    setMessages(current => [...current.filter(value => value.message !== message || value.kind !== kind), item].slice(-5))
  }, [])
  const api = useMemo<ToastApi>(() => ({
    success: (message, options) => show('success', message, options),
    error: (message, options) => show('error', message, options),
    warning: (message, options) => show('warning', message, options),
    info: (message, options) => show('info', message, options),
  }), [show])

  useEffect(() => {
    // Render inside the active dialog so notifications remain visible and operable above its backdrop.
    let stack: HTMLDialogElement[] = []
    const update = () => {
      const open = Array.from(document.querySelectorAll<HTMLDialogElement>('dialog[open]'))
      stack = [...stack.filter(dialog => open.includes(dialog)), ...open.filter(dialog => !stack.includes(dialog))]
      setTarget(stack.at(-1) ?? document.body)
    }
    update()
    const observer = new MutationObserver(update)
    observer.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['open'] })
    return () => observer.disconnect()
  }, [])

  return <ToastContext.Provider value={api}>{children}{createPortal(
    <section className="toast-viewport" aria-label="Notifications">
      <div aria-live="polite" aria-relevant="additions">
        {messages.filter(item => item.kind !== 'error').map(item => <Toast key={item.id} item={item} onDismiss={dismiss} />)}
      </div>
      <div aria-live="assertive" aria-relevant="additions">
        {messages.filter(item => item.kind === 'error').map(item => <Toast key={item.id} item={item} onDismiss={dismiss} />)}
      </div>
    </section>, target,
  )}</ToastContext.Provider>
}

function Toast({ item, onDismiss }: { item: ToastMessage; onDismiss: (id: number) => void }) {
  const [hovered, setHovered] = useState(false)
  const [focused, setFocused] = useState(false)
  const [hidden, setHidden] = useState(document.hidden)
  const remaining = useRef(item.duration)
  useEffect(() => {
    const update = () => setHidden(document.hidden)
    document.addEventListener('visibilitychange', update)
    return () => document.removeEventListener('visibilitychange', update)
  }, [])
  useEffect(() => {
    if (item.duration <= 0 || hovered || focused || hidden) {
      return
    }
    const started = performance.now()
    const timer = window.setTimeout(() => onDismiss(item.id), remaining.current)
    return () => {
      window.clearTimeout(timer)
      remaining.current = Math.max(0, remaining.current - (performance.now() - started))
    }
  }, [item.id, item.duration, hovered, focused, hidden, onDismiss])
  const Icon = { success: CheckCircle2, error: CircleAlert, warning: TriangleAlert, info: Info }[item.kind]
  const title = { success: 'Success', error: 'Error', warning: 'Warning', info: 'Information' }[item.kind]
  return <article className={`toast toast-${item.kind}`} onMouseEnter={() => setHovered(true)} onMouseLeave={() => setHovered(false)}
    onFocusCapture={() => setFocused(true)} onBlurCapture={event => {
      if (!event.currentTarget.contains(event.relatedTarget)) {
        setFocused(false)
      }
    }}>
    <Icon size={20} className="toast-icon" aria-hidden="true" />
    <div className="toast-content"><strong>{title}</strong><p>{item.message}</p></div>
    <button type="button" className="ghost icon-only" aria-label={`Dismiss ${title.toLowerCase()} notification`} onClick={() => onDismiss(item.id)}><X size={16} aria-hidden="true" /></button>
  </article>
}
