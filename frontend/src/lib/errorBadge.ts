import { formatDuration } from '../constants/format'

/**
 * The text on a configuration's persistent error badge, given its stored message and timestamp.
 *
 * `SetErrorAsync` and `SetNormalAsync` (`BackupConfigService`) are the only two things that touch this
 * state: a run failing sets it, a run succeeding or a manual Reset clears it. A pause, a suspend and a
 * resume touch none of it — so an error from three days ago survives every one of them in between, and
 * without a timestamp on screen it reads exactly like one from a minute ago. That is the bug this
 * function exists to fix. It does not decide *whether* the badge shows — the caller already knows that
 * from `BackupConfig.status` — only what it says once it does.
 *
 * `lastError` is the primary signal, the same way `pause` is the primary signal in `pauseDisplay`: null
 * means there is nothing to report and the rest does not matter. The caller should only reach this
 * function when there already is an error, but a pure function should not assume its caller got the
 * gate right, so it answers the question honestly either way.
 *
 * The case that decides the rest of the shape is a `lastError` with **no** `lastErrorAt`: a row written
 * before that column existed, or a config synced from a backend old enough to never have sent it. Losing
 * the timestamp there is fine; dropping the whole badge because one of its two fields is missing is not
 * — the fact that matters most (something failed) would vanish along with the fact that matters less
 * (when). So a missing timestamp still renders the bare label instead of nothing.
 */
export function errorBadgeLabel(
  lastError: string | null,
  lastErrorAt: string | null,
  now: Date = new Date(),
): string | null {
  if (!lastError) return null
  if (!lastErrorAt) return 'Error'
  const elapsedSeconds = (now.getTime() - new Date(lastErrorAt).getTime()) / 1000
  return `Error — ${formatDuration(elapsedSeconds)} ago`
}
