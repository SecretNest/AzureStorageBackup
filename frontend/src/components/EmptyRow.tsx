import type { ReactNode } from 'react'

/**
 * The single-cell row a table shows in place of its rows, for the two reasons a table has none.
 *
 * Those two reasons are **not** the same thing and must not render the same way. A list state
 * initialised to `[]` is empty before its first fetch resolves and empty after a fetch that genuinely
 * returned nothing, so a bare `length === 0` renders "No backups yet." while the request is still in
 * flight — every page announced that the user had nothing, then replaced it with their data a moment
 * later. On a cold load, or a slow one, that is the first thing they read.
 *
 * So the caller passes what it knows: `loaded` is false until the fetch has come back, whatever it
 * came back with. Only then may the empty message be shown, because only then is it true.
 *
 * "Loading…" rather than nothing, so the space does not collapse and reflow the moment data lands —
 * and because a table with a header and a blank body reads as broken. This is the same choice
 * ContainersPage already made for its non-table list.
 *
 * The `empty-state` class carries the phone-tier rules that keep this out of the card layout
 * (index.css: no card border, no `::before` label), which is why the cell keeps it in both states.
 */
export function EmptyRow({
  loaded,
  colSpan,
  children,
}: {
  loaded: boolean
  colSpan: number
  children: ReactNode
}) {
  return (
    <tr>
      <td colSpan={colSpan} className="empty-state">
        {loaded ? children : 'Loading…'}
      </td>
    </tr>
  )
}
