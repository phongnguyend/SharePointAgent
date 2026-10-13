import { useEffect, useId, useRef, type ReactNode } from 'react'
import { X } from 'lucide-react'

export function ModalHeader({ title, titleId, icon, actions, onClose }: {
  title: string
  titleId: string
  icon?: ReactNode
  actions?: ReactNode
  onClose: () => void
}) {
  return <header className="modal-head">
    <h2 id={titleId} title={title}>{icon}{title}</h2>
    <div className="modal-header-actions">{actions ?? <button type="button" className="ghost icon-only" onClick={onClose} aria-label="Close">
      <X size={15} aria-hidden="true" />
    </button>}</div>
  </header>
}

export function ModalBody({ children, className = '' }: { children: ReactNode; className?: string }) {
  return <div className={`modal-body ${className}`.trim()}>{children}</div>
}

export function ModalFooter({ children }: { children: ReactNode }) {
  return <footer className="modal-foot">{children}</footer>
}

/** App-styled dialog. The native element supplies focus trapping, focus restoration and inertness. */
function ModalRoot({ open, title, icon, className, bodyClassName, onClose, children, footer, headerActions }: {
  open: boolean
  title: string
  icon?: ReactNode
  className?: string
  bodyClassName?: string
  onClose: () => void
  children: ReactNode
  footer?: ReactNode
  headerActions?: ReactNode
}) {
  const ref = useRef<HTMLDialogElement>(null)
  const titleId = useId()
  useEffect(() => {
    const dialog = ref.current
    if (!dialog) {
      return
    }
    if (open && !dialog.open) {
      dialog.showModal()
    } else if (!open && dialog.open) {
      dialog.close()
    }
    return () => {
      if (dialog.open) {
        dialog.close()
      }
    }
  }, [open])

  return <dialog ref={ref} className={className ? `modal ${className}` : 'modal'} aria-labelledby={titleId}
    onCancel={event => {
      // A file input's cancel event must not dismiss the surrounding modal.
      if (event.target !== event.currentTarget) {
        return
      }
      event.preventDefault()
      onClose()
    }}>
    <ModalHeader title={title} titleId={titleId} icon={icon} actions={headerActions} onClose={onClose} />
    <ModalBody className={bodyClassName}>{children}</ModalBody>
    {footer !== null && <ModalFooter>{footer ?? <button type="button" onClick={onClose}><X size={14} aria-hidden="true" />Close</button>}</ModalFooter>}
  </dialog>
}

export const Modal = Object.assign(ModalRoot, { Header: ModalHeader, Body: ModalBody, Footer: ModalFooter })
