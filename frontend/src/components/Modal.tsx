import { useEffect, useRef, type ReactNode } from 'react'

// 当前挂载的弹窗按打开顺序排成一个栈，栈顶就是最上面那一层。
// 用栈而不是计数器，是因为要回答的问题不止"还有没有弹窗开着"，
// 还有"哪一个在最上面"：还原对话框会在自身之上再开 PathBrowser，
// 两者都各自挂了 keydown 监听，只数数量分不清谁该响应 Esc——
// 一次 Esc 会把两层一起关掉。栈顶元素的身份是可查的，
// 于是"谁该响应 Esc"和"背景滚动锁还要不要保留"（栈空即最后一个关闭）
// 用同一份状态就能同时回答，不必再维护第二份计数。
const modalStack: symbol[] = []
let restoreOverflow = ''

function useModalLayering(id: symbol, onClose: () => void) {
  // 入栈/出栈与滚动锁定合并在一个 effect 里：谁把栈从空变为非空，
  // 谁负责保存原始 overflow 再锁定；谁把栈从非空清空到空，谁负责恢复。
  // cleanup 时不假设自己一定在栈顶——卸载顺序可能与打开顺序不一致，
  // 所以用 indexOf 定位再 splice，而不是直接 pop。
  // id 在整个挂载期间不变（来自调用方的 idRef），特意保持依赖为空数组，
  // 只在挂载/卸载各跑一次——这正是滚动锁定需要的语义。
  /* oxlint-disable react-hooks/exhaustive-deps */
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
  }, [])
  /* oxlint-enable react-hooks/exhaustive-deps */

  // Esc 关闭。只有栈顶（最上面那层）才响应，避免嵌套弹窗时一次 Esc 关掉两层。
  // id 同样在挂载期间不变，依赖数组只跟随 onClose 变化。
  /* oxlint-disable react-hooks/exhaustive-deps */
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape' && modalStack[modalStack.length - 1] === id) onClose()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [onClose])
  /* oxlint-enable react-hooks/exhaustive-deps */
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
  // 稳定的身份标识，惰性初始化避免 React 19 下 useRef(null!) 的类型顾虑，
  // 也不用随机数/自增整数——前者可能撞，后者在 StrictMode 双挂载下会产生
  // 不符合预期的值。
  const idRef = useRef<symbol | null>(null)
  if (idRef.current === null) idRef.current = Symbol('modal')

  // 手机全屏时遮罩不可见，点外面关不掉，Esc 是必须的退路；桌面上这也是弹窗的常规行为。
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
