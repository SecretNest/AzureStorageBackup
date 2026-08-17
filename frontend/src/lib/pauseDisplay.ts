import { PauseSource, type PauseInfo } from '../api/backupConfigs'

/**
 * What a paused run renders as, and which actions it offers.
 *
 * The gate can be held closed for two reasons and they call for different buttons: a transient-error
 * pause is counting down a timer the operator may skip (Retry now), while a user pause is waiting on the
 * operator and has no timer at all (Resume). Rendering one as the other puts a button on screen that
 * cannot do anything — that is the whole reason this is a function with its own tests rather than a
 * condition buried in the row.
 *
 * `pause.source` alone cannot make this call. Pressing Pause while a transient-error backoff already
 * holds the gate leaves `source` reporting TransientError — with its own reason, failure count and
 * countdown — until that backoff's own timer fires (see `PauseGate.ReleaseLocked`), up to one steady
 * interval, five minutes by default. `pausedByUser` is the sibling field (on the run, not on `pause`
 * itself — see `BackupRun.pausedByUser`) that says the operator's own hold is standing *regardless* of
 * what `source` currently reports, and it is what this function keys its decision on. Two sub-cases
 * follow from a standing hold:
 *
 * - nothing else wrong (`source` has already caught up to User, or was never anything else) → plain
 *   "Paused", offer Resume;
 * - still composed with a live backoff (`source` is still TransientError) → say both, because the run
 *   really is both held by the operator and failing underneath, and the design keeps the two facts
 *   separate rather than letting either overwrite the other. Retry now is not offered even though a
 *   backoff is technically running: its timer does not release anyone while the hold stands (it only
 *   relabels this line back to plain "Paused" once it fires), so the button would still do nothing.
 *
 * A pause with no `source` at all comes from a backend older than this field, and reads as a transient
 * error because that is what every pause was before — assuming the other way would offer Resume on a run
 * nobody paused.
 */
export function pauseDisplay(
  pause: PauseInfo | null,
  pausedByUser: boolean,
): { label: string; canResume: boolean; canRetryNow: boolean } | null {
  if (!pause)
    return null

  if (pausedByUser) {
    const stillBackingOff = pause.source === PauseSource.TransientError
    return {
      label: stillBackingOff ? `Paused — also still hitting an error: ${pause.reason}` : 'Paused',
      canResume: true,
      canRetryNow: false,
    }
  }

  return { label: `Paused — ${pause.reason}`, canResume: false, canRetryNow: true }
}
