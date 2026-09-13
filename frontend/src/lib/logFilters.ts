import { formatLocalDateTime } from '../constants/format'

/**
 * The Logs page's four filter controls as one value, so that "what a fresh page starts with" and
 * "put everything back" are each a single expression instead of four `setState` calls that can drift
 * apart — the page had no way back at all: clearing a `datetime-local` box means clearing each of its
 * segments by hand, and until both boxes were empty the table kept showing a filtered slice.
 */
export interface LogFilters {
  minLevel: number | ''
  source: string
  from: string
  to: string
}

/** Every control blank: the query carries no bounds and the table shows everything. */
export const noLogFilters: LogFilters = { minLevel: '', source: '', from: '', to: '' }

/**
 * An instant as `<input type="datetime-local">` takes it: `YYYY-MM-DDTHH:mm` in the browser's own
 * zone. Built from the local getters rather than from `toISOString`, which would hand the control a
 * UTC wall clock — off by the reader's offset, silently, since both forms parse.
 *
 * No seconds: the control's default step is one minute, and a value carrying seconds is normalised
 * away the moment the operator touches the box.
 */
export function datetimeLocalValue(at: Date): string {
  const pad = (n: number) => String(n).padStart(2, '0')
  return (
    `${at.getFullYear()}-${pad(at.getMonth() + 1)}-${pad(at.getDate())}` +
    `T${pad(at.getHours())}:${pad(at.getMinutes())}`
  )
}

/**
 * What the page opens with: **To** already holding the current time, the rest blank.
 *
 * Requested because the one thing the field exists for — "Delete before To" — needed the whole
 * timestamp typed by hand before the button would do anything. The list is unaffected in practice:
 * no log entry is in the future, so a bound at "now" excludes nothing that exists.
 */
export function initialLogFilters(now: Date): LogFilters {
  return { ...noLogFilters, to: datetimeLocalValue(now) }
}

/**
 * The **To** a refresh should query with. While the box still holds what the page put there, refresh
 * means "up to now" and the value follows the clock; once the operator has decided what To is — typed
 * a time, or cleared the box — it is theirs and refresh leaves it alone.
 *
 * Without this, a page opened at nine would keep answering "up to nine o'clock" for the rest of the
 * day, and an entry written while the page was open would never appear however often it was refreshed.
 */
export function toForRefresh(current: string, pinned: boolean, now: Date): string {
  return pinned ? current : datetimeLocalValue(now)
}

/** What to show beside a time box: nothing, the value as the table writes it, or a complaint. */
export interface TimeFieldHint {
  text: string
  incomplete: boolean
}

/**
 * The line beside a `datetime-local` box.
 *
 * Two things go wrong with that control and neither is visible from its own face. It renders in the
 * browser's locale, so an en-US reader types `07:30 PM` into a filter whose results are listed as
 * `19:30` — same instant, two clocks, no way to tell from the screen that they agree. And a box the
 * operator has half filled in reports its value as the empty string: the filter silently becomes "no
 * bound", the query widens to everything, and the refresh that ran looks like a refresh that did not.
 *
 * So the box gets an echo. When it parses, the echo is the value in exactly the format the table uses
 * below it, which answers the first. When it does not — `badInput`, the control's own word for "there
 * is something in here that is not a time" — the echo says so, which answers the second.
 */
export function timeFieldHint(value: string, badInput: boolean): TimeFieldHint | null {
  if (badInput)
    return { text: 'incomplete — fill in the date and the time', incomplete: true }
  if (!value)
    return null
  return { text: formatLocalDateTime(value), incomplete: false }
}

/**
 * The page-level notice for a half-filled box, naming which one. Said above the table because the
 * consequence is there: the query is not run at all while a bound is unreadable, so the table below
 * holds the previous result rather than silently widening to everything.
 */
export function incompleteTimeNotice(fromBad: boolean, toBad: boolean): string | null {
  if (fromBad && toBad)
    return 'The From and To times are incomplete — fill in both, or clear them, to run the query.'
  if (fromBad)
    return 'The From time is incomplete — fill in the date and the time, or clear the box, to run the query.'
  if (toBad)
    return 'The To time is incomplete — fill in the date and the time, or clear the box, to run the query.'
  return null
}
