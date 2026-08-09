import type { ReactNode } from 'react'

// A form field row, shared by pages, modals and dialogs.
// There used to be four different copies (label widths of 130/140/200, inconsistent alignment),
// one of the main reasons the UI looked ragged.
export function Field({
  label,
  children,
  multi,
}: {
  label: string
  children: ReactNode
  /**
   * Must be set when the field contains **more than one** control.
   *
   * Wrapping a row in <label> is meant to give "click the label to focus the control", but its
   * activation lands on the first labelable descendant only. With several controls that makes the
   * label a lie, and it also misfires: dragging a textarea's resize handle puts mousedown and
   * mouseup on different elements, so the browser dispatches click to their common ancestor — this
   * <label> — which re-ticks the "Use default" box in front of it. That is what was reported.
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
