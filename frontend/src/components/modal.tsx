import type { ReactNode } from 'react'

// 表单字段行，供各页面与 Modal/Dialog 复用。
// 曾经有四份各不相同的副本（label 宽 130/140/200、对齐方式不一），是界面参差不齐的主因之一。
export function Field({ label, children }: { label: string; children: ReactNode }) {
  return (
    <label className="field">
      <span className="field-label">{label}</span>
      <span>{children}</span>
    </label>
  )
}
