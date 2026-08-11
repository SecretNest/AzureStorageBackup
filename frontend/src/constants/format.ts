const pad = (n: number) => String(n).padStart(2, '0')

/**
 * A UTC instant as `YYYY-MM-DD HH:mm:ss` in the browser's timezone.
 *
 * Built from the local getters (getFullYear/getHours/…), never from the ISO string, so the value on
 * screen is the reader's wall clock and not the UTC one the backend stores. Log lines are read next
 * to each other and sorted by eye, so the layout is fixed-width and locale-independent rather than
 * toLocaleString's "8/11/2026, 8:00:00 PM".
 */
export function formatLocalDateTime(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return iso // never swallow a value the backend sent
  return (
    `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ` +
    `${pad(d.getHours())}:${pad(d.getMinutes())}:${pad(d.getSeconds())}`
  )
}

/**
 * The browser's UTC offset, as `UTC+08:00`. Shown wherever a screen states or takes a time, so that
 * "is this UTC or mine?" is answered on the screen instead of being guessed — and a browser that is
 * genuinely set to UTC says so outright rather than looking like a backend bug.
 *
 * getTimezoneOffset counts minutes *behind* UTC, hence the flipped sign; the offset is taken for the
 * given instant, so a DST switch labels each side with the offset that actually applied.
 */
export function formatUtcOffset(at: Date): string {
  const minutes = -at.getTimezoneOffset()
  const sign = minutes < 0 ? '-' : '+'
  const abs = Math.abs(minutes)
  return `UTC${sign}${pad(Math.floor(abs / 60))}:${pad(abs % 60)}`
}

/**
 * A version's start and end. Times are stored in UTC and rendered in the browser's timezone.
 *
 * The date is written once for a single **local** date (and the comparison must be made in local
 * time — comparing UTC dates makes two backups on the same local day print two dates whenever they
 * straddle UTC midnight); across midnight, both sides spell it out. Versions written before the
 * upgrade have no start time and get an em dash rather than some other time standing in for it.
 */
export function formatVersionSpan(startedAt: string | null, completedAt: string): string {
  const end = new Date(completedAt)
  if (!startedAt) return `— → ${end.toLocaleString()}`
  const start = new Date(startedAt)
  const sameDay = start.toLocaleDateString() === end.toLocaleDateString()
  return `${start.toLocaleString()} → ${sameDay ? end.toLocaleTimeString() : end.toLocaleString()}`
}

/**
 * A human-readable remaining duration, with a unit on every segment.
 *
 * Do not slice .NET's TimeSpan string: it serialises as `[d.]hh:mm:ss[.fffffff]`, and the dot after
 * the day count is indistinguishable from the one before fractional seconds. `split('.')[0]` was
 * once used to drop the fraction, and anything over a day collapsed to just the day number — 3 days
 * 5 hours rendered as a bare "~3 left", with no unit and no way to tell days from hours.
 * The seconds come straight from the backend (etaSeconds), where that ambiguity does not exist.
 *
 * Two units only: "3d 5h" reads better than "3d 5h 20m 11s", and with three days left nobody cares
 * about the 11 seconds.
 */
export function formatDuration(seconds: number): string {
  const s = Math.max(0, Math.round(seconds))
  const d = Math.floor(s / 86400)
  const h = Math.floor((s % 86400) / 3600)
  const m = Math.floor((s % 3600) / 60)
  if (d > 0) return h > 0 ? `${d}d ${h}h` : `${d}d`
  if (h > 0) return m > 0 ? `${h}h ${m}m` : `${h}h`
  if (m > 0) return `${m}m ${s % 60}s`
  return `${s}s`
}

/** Human-readable byte counts. Used all over the backup UI (sizes, speeds); centralised so the copies cannot drift. */
export function formatBytes(n: number): string {
  if (n < 1024) return `${n} B`
  const units = ['KB', 'MB', 'GB', 'TB']
  let v = n / 1024
  let i = 0
  while (v >= 1024 && i < units.length - 1) {
    v /= 1024
    i++
  }
  return `${v.toFixed(1)} ${units[i]}`
}
