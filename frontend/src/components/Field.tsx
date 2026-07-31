import type { ReactNode } from 'react'

// 表单字段行，供各页面与 Modal/Dialog 复用。
// 曾经有四份各不相同的副本（label 宽 130/140/200、对齐方式不一），是界面参差不齐的主因之一。
export function Field({
  label,
  children,
  multi,
}: {
  label: string
  children: ReactNode
  /**
   * 该字段里有**多个**控件时必须置位。
   *
   * <label> 包住一行本来是为了「点标题即聚焦控件」，但它的激活行为只落在第一个可标注的
   * 后代上。字段里有多个控件时，这既让标题名不副实，又会误触发：拖动 textarea 的缩放柄时，
   * mousedown 与 mouseup 落在不同元素上，浏览器把 click 派发到二者的共同祖先——也就是这个
   * <label>——于是把它前面那个「Use default」勾了回去。用户报的正是这个现象。
   */
  multi?: boolean
}) {
  const inner = (
    <>
      <span className="field-label">{label}</span>
      <span>{children}</span>
    </>
  )
  return multi ? <div className="field">{inner}</div> : <label className="field">{inner}</label>
}
