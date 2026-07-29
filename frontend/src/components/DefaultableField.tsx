import type { ReactNode } from 'react'
import { Field } from './modal'

/**
 * 可继承字段的一行（PRD §3「使用默认」）。勾选 = 该字段存 null，运行时读全局设置；
 * 取消勾选 = 显示控件并存具体值。
 *
 * 勾选状态下不渲染控件，只显示当前生效值——留着一个隐藏的草稿值，会让界面显示的
 * 与将要保存的不一致。
 */
export function DefaultableField({
  label,
  useDefault,
  onToggle,
  effectiveText,
  children,
}: {
  label: string
  useDefault: boolean
  onToggle: (useDefault: boolean) => void
  effectiveText: string
  children: ReactNode
}) {
  return (
    <Field label={label} multi>
      <span className="defaultable">
        <label className="defaultable-toggle">
          <input
            type="checkbox"
            checked={useDefault}
            onChange={(e) => onToggle(e.target.checked)}
          />
          Use default
        </label>
        {/* defaultable-effective：与勾选行同高，好让这一行文字跟左边的标题落在同一条中线上。 */}
        {useDefault ? <span className="defaultable-effective text-muted">{effectiveText}</span> : children}
      </span>
    </Field>
  )
}
