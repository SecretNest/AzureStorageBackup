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
