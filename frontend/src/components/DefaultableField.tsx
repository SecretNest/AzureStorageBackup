import type { ReactNode } from 'react'
import { Field } from './Field'

/**
 * One row for an inheritable field (PRD §3, "use default"). Ticked = the field stores null and
 * the global setting is read at run time; unticked = show the control and store a concrete value.
 *
 * While ticked, the control is not rendered at all and only the effective value is shown — keeping
 * a hidden draft value would make what is displayed differ from what will be saved.
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
        {/* defaultable-effective: same height as the checkbox row, so this line of text sits on the same centreline as the label to its left. */}
        {/* defaultable-control: one flex item for the whole control side, however many controls it holds. Without it a
            field with two controls (the rule lists: a case-sensitive box and the case-insensitive one under it) hands
            .defaultable two flex items, and the second wraps onto its own line flush with the left edge of the row —
            under "Use default" rather than under the box it belongs to. */}
        {useDefault ? (
          <span className="defaultable-effective text-muted">{effectiveText}</span>
        ) : (
          <span className="defaultable-control">{children}</span>
        )}
      </span>
    </Field>
  )
}
