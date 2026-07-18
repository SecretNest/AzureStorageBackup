import type { ReactNode } from 'react'

// 表单字段行组件，供各 Modal/Dialog/表单复用。
export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', margin: '0.4rem 0' }}>
      <span style={{ width: 200, display: 'inline-block' }}>{label}</span>
      {children}
    </label>
  )
}
