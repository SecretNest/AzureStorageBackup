import { useEffect, useRef, type ReactNode } from 'react'

// Mounted dialogs form a stack in open order, and the top of the stack is the frontmost layer.
// A stack rather than a counter, because the question is not only "is any dialog still open?" but
// also "which one is on top?": the restore dialog opens PathBrowser above itself and both attach a
// keydown listener, so counting alone cannot tell which should answer Esc — one press would close
// both. The top element's identity is queryable, so "who answers Esc" and "should the background
// scroll lock stay" (empty stack means the last one closed) are answered from the same state,
// with no second counter to maintain.
const modalStack: symbol[] = []
let restoreOverflow = ''

function useModalLayering(id: symbol, onClose: () => void) {
  // Pushing/popping and the scroll lock share one effect: whoever takes the stack from empty to
  // non-empty saves the original overflow and locks; whoever empties it restores.
  // The cleanup does not assume it is on top — unmount order can differ from open order — so it
  // locates itself with indexOf and splices rather than popping.
  useEffect(() => {
    if (modalStack.length === 0) {
      restoreOverflow = document.body.style.overflow
      document.body.style.overflow = 'hidden'
    }
    modalStack.push(id)
    return () => {
      const idx = modalStack.indexOf(id)
      if (idx !== -1) modalStack.splice(idx, 1)
      if (modalStack.length === 0) document.body.style.overflow = restoreOverflow
    }
  }, [id])

  // Esc closes. Only the top of the stack responds, so one Esc cannot close two nested dialogs at once.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && modalStack[modalStack.length - 1] === id) onClose()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [onClose, id])
}

/**
 * The dialog shell. The three-part structure (title bar / body / action bar) is the skeleton of the
 * full-screen panel on phones: the title and action bars are fixed and only the middle scrolls —
 * otherwise a long form's "save" ends up several screens away. Desktop looks the same as before.
 */
export function Modal({
  title,
  onClose,
  footer,
  secondary,
  children,
}: {
  title: ReactNode
  onClose: () => void
  footer?: ReactNode
  /** Set when stacked on top of another dialog, to use a higher layer. */
  secondary?: boolean
  children: ReactNode
}) {
  // A stable identity, lazily initialised to avoid the typing awkwardness of useRef(null!) under
  // React 19, and not a random number or an incrementing integer — the former can collide, the
  // latter produces surprising values under StrictMode's double mount.
  const idRef = useRef<symbol | null>(null)
  if (idRef.current === null) idRef.current = Symbol('modal')

  // On a full-screen phone dialog the backdrop is invisible and cannot be clicked away, so Esc is the required exit; on desktop it is standard behaviour anyway.
  useModalLayering(idRef.current, onClose)

  return (
    <div
      className={secondary ? 'modal-overlay modal-overlay-secondary' : 'modal-overlay'}
      onClick={onClose}
    >
      <div className="modal-panel" onClick={(e) => e.stopPropagation()}>
        <div className="modal-header">
          <h3>{title}</h3>
          <button type="button" className="icon-btn modal-close" onClick={onClose} aria-label="Close">
            ✕
          </button>
        </div>
        <div className="modal-body">{children}</div>
        {footer && <div className="modal-footer">{footer}</div>}
      </div>
    </div>
  )
}
