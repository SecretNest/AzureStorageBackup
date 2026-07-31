import { useEffect, type ReactNode } from 'react'

// 开着的弹窗数量。锁定 body 滚动必须计数，不能各自为政：
// 还原对话框会在自身之上再开 PathBrowser，内层关闭时若直接恢复 overflow，
// 外层还开着，背景就又能滚了。
let openCount = 0
let restoreOverflow = ''

function useModalScrollLock() {
  useEffect(() => {
    if (openCount === 0) {
      restoreOverflow = document.body.style.overflow
      document.body.style.overflow = 'hidden'
    }
    openCount += 1
    return () => {
      openCount -= 1
      if (openCount === 0) document.body.style.overflow = restoreOverflow
    }
  }, [])
}

/**
 * 弹窗外壳。三段结构（标题栏 / 内容 / 动作栏）在手机上是全屏面板的骨架：
 * 标题栏与动作栏固定，只有中间滚动——否则长表单的"保存"会在几屏以外。
 * 桌面端外观与手写这套结构时一致。
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
  /** 叠在另一个弹窗之上时置位，用更高的层级。 */
  secondary?: boolean
  children: ReactNode
}) {
  useModalScrollLock()

  // Esc 关闭。手机全屏时遮罩不可见，点外面关不掉；桌面上这也是弹窗的常规行为。
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [onClose])

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
