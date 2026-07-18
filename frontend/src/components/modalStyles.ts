import type { CSSProperties } from 'react'

// 弹窗共用样式常量，从组件文件中拆出（纯常量，不含组件），供各 Modal/Dialog 复用。
export const overlayStyle: CSSProperties = {
  position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)',
  display: 'flex', alignItems: 'flex-start', justifyContent: 'center', paddingTop: '4vh', zIndex: 50,
}
export const panelStyle: CSSProperties = {
  background: '#fff', padding: '1.5rem', borderRadius: 6, minWidth: 620, maxWidth: '90vw',
  maxHeight: '88vh', overflow: 'auto',
}
